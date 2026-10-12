// 登録データ（ポケモン・チーム）のデータ連携 API。
//
// サイト本体（静的アセット）は今までどおり Cloudflare が配信し、/api/* だけがこのスクリプトを通る
// （wrangler.jsonc の assets.run_worker_first）。
//
//   GET    /api/roster  → 200 {"revision": n, "roster": {...} | null}
//   PUT    /api/roster  ← {"baseRevision": n, "roster": {...}}
//                       → 200 {"revision": n+1} / 409 {"revision": 現在, "roster": 現在}
//   DELETE /api/roster  → 204（キーの作り直しで古い置き場を消す。以後そのキーは 410）
//
// 認証は "Authorization: Bearer <アクセスキー>"。アクセスキーは 256 bit の乱数を base64url（パディングなし・43 文字）にしたもの。
// サーバーはキーそのものを保存しない。SHA-256(キー) の 16 進を Durable Object の名前にして、キーごとに 1 つの置き場を持つ。
// キーはログ・エラーメッセージ・URL に出さない（このファイルでは console.* を使わない）。

import { DurableObject } from "cloudflare:workers";

const ROSTER_PATH = "/api/roster";
const KEY_PATTERN = /^[A-Za-z0-9_-]{43}$/;
/** PUT の本文の上限（UTF-8 のバイト数）。 */
const MAX_BODY_BYTES = 256 * 1024;

const BASE_HEADERS = {
  "Cache-Control": "no-store",
  "X-Content-Type-Options": "nosniff",
};

function json(status, body, extraHeaders) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: {
      ...BASE_HEADERS,
      ...(body === undefined ? {} : { "Content-Type": "application/json; charset=utf-8" }),
      ...extraHeaders,
    },
  });
}

function error(status, code, message, extraHeaders) {
  return json(status, { error: code, message }, extraHeaders);
}

/** "Bearer <key>" から形式の正しいキーを取り出す。正しくなければ null。 */
function readKey(request) {
  const header = request.headers.get("Authorization");
  if (!header) return null;
  const match = /^Bearer ([^\s]+)$/i.exec(header);
  if (!match) return null;
  return KEY_PATTERN.test(match[1]) ? match[1] : null;
}

async function sha256Hex(text) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

class TooLargeError extends Error {}

/** 本文を上限付きで読む（上限を超えたら読むのをやめる）。 */
async function readBodyLimited(request, limit) {
  const declared = request.headers.get("Content-Length");
  if (declared !== null && Number(declared) > limit) throw new TooLargeError();
  if (!request.body) return new Uint8Array(0);
  const reader = request.body.getReader();
  const chunks = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > limit) {
      await reader.cancel().catch(() => {});
      throw new TooLargeError();
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const c of chunks) {
    bytes.set(c, offset);
    offset += c.byteLength;
  }
  return bytes;
}

function isPlainObject(v) {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

/** PUT の本文を検証して {baseRevision, rosterJson} を返す。不正なら Response を返す。 */
function parsePutBody(bytes) {
  let body;
  try {
    body = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  } catch {
    return error(400, "invalid_json", "本文が JSON ではありません");
  }
  if (!isPlainObject(body)) return error(400, "invalid_body", "本文はオブジェクトにしてください");
  const { baseRevision, roster } = body;
  if (!Number.isSafeInteger(baseRevision) || baseRevision < 0)
    return error(400, "invalid_revision", "baseRevision は 0 以上の整数にしてください");
  if (!isPlainObject(roster)) return error(400, "invalid_roster", "roster はオブジェクトにしてください");
  // 受け取った値をそのまま文字列に戻して保存する（JSON として正しいものだけが残る）
  return { baseRevision, rosterJson: JSON.stringify(roster) };
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (!url.pathname.startsWith("/api/")) {
      // run_worker_first の対象外（通常は来ない）。静的アセットに任せる
      return env.ASSETS.fetch(request);
    }
    try {
      return await handleApi(request, env, url);
    } catch {
      // 詳細（キーを含む可能性がある）は返さない
      return error(500, "internal_error", "サーバーでエラーが発生しました");
    }
  },
};

async function handleApi(request, env, url) {
  if (url.pathname !== ROSTER_PATH) return error(404, "not_found", "見つかりません");
  const method = request.method;
  if (method !== "GET" && method !== "PUT" && method !== "DELETE")
    return error(405, "method_not_allowed", "使えないメソッドです", { Allow: "GET, PUT, DELETE" });

  const key = readKey(request);
  if (key === null)
    return error(401, "unauthorized", "アクセスキーが正しくありません", { "WWW-Authenticate": 'Bearer realm="roster"' });

  // Durable Object にはキーを渡さない（名前は SHA-256 のみ）
  const stub = env.ROSTER.get(env.ROSTER.idFromName(await sha256Hex(key)));

  if (method === "GET") return stub.getRoster();
  if (method === "DELETE") return stub.deleteRoster();

  let bytes;
  try {
    bytes = await readBodyLimited(request, MAX_BODY_BYTES);
  } catch (e) {
    if (e instanceof TooLargeError) return error(413, "too_large", `データが大きすぎます（上限 ${MAX_BODY_BYTES / 1024} KB）`);
    throw e;
  }
  const parsed = parsePutBody(bytes);
  if (parsed instanceof Response) return parsed;
  return stub.putRoster(parsed.baseRevision, parsed.rosterJson);
}

/**
 * アクセスキー 1 つ分の置き場（SQLite ストレージの Durable Object）。
 * 名前ごとにインスタンスは 1 つだけで、リクエストは 1 つずつ処理されるので、revision の比較と保存の間に別の書き込みは入らない。
 *
 * ストレージ: "revision"（数値）・"roster"（JSON 文字列）・"deleted"（キーの作り直しで消した印）
 */
export class RosterSlot extends DurableObject {
  async #read() {
    const values = await this.ctx.storage.get(["revision", "roster", "deleted"]);
    return {
      revision: values.get("revision") ?? 0,
      roster: values.get("roster") ?? null,
      deleted: values.get("deleted") === true,
    };
  }

  #gone() {
    return error(410, "key_retired", "このアクセスキーは作り直されたため使えません。新しいアクセスキーを使ってください");
  }

  #current(status, revision, rosterJson) {
    // roster は保存済みの JSON 文字列をそのまま埋め込む（JSON.stringify で作ったものなので安全）
    return new Response(`{"revision":${revision},"roster":${rosterJson ?? "null"}}`, {
      status,
      headers: { ...BASE_HEADERS, "Content-Type": "application/json; charset=utf-8" },
    });
  }

  async getRoster() {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    return this.#current(200, s.revision, s.roster);
  }

  async putRoster(baseRevision, rosterJson) {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    if (baseRevision !== s.revision) return this.#current(409, s.revision, s.roster);
    const revision = s.revision + 1;
    await this.ctx.storage.put({ revision, roster: rosterJson });
    return json(200, { revision });
  }

  async deleteRoster() {
    await this.ctx.storage.deleteAll();
    await this.ctx.storage.put("deleted", true);
    return json(204);
  }
}
