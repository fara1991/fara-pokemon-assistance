// 登録データ（ポケモン・チーム）のデータ共有 API。
//
// サイト本体（静的アセット）は今までどおり Cloudflare が配信し、/api/my/* だけがこのスクリプトを通る
// （wrangler.jsonc の assets.run_worker_first）。ゲームデータ（CSV）の /api/game/* は静的アセットで、ここには来ない。
//
// 登録データはゲーム（データセット: Champions・Gen9 など）ごとに分かれているので、本文の dataSets はゲームのキーごとのオブジェクト。
//
//   GET    /api/my/pokemon → 200 {"revision": n, "dataSets": {"Champions": {"nextId": n, "pokemon": [...]}, ...}}
//   PUT    /api/my/pokemon ← {"baseRevision": n, "dataSets": {"Champions": {"pokemon": [...], "nextId"?: n}}}
//                           → 200 {"revision": n+1} / 409 {"revision": 現在, "dataSets": 現在}
//          書いたゲームのポケモンだけを置き換える（書かなかったゲームはそのまま）。同じ処理の中で、消えたポケモンをそのゲームの
//          すべてのチームから外し、0 体になったチームを消し、使用中のチームが消えたら使用中を解除する。
//   GET    /api/my/team    → 200 {"revision": n, "dataSets": {"Champions": {"teams": [...], "activeTeam": n | null}, ...}}
//   PUT    /api/my/team    ← {"baseRevision": n, "dataSets": {"Champions": {"teams": [...], "activeTeam": n | null}}}
//                           → 200 {"revision": n+1} / 409 {"revision": 現在, "dataSets": 現在}
//          書いたゲームのチームと使用中チームだけを置き換える。登録に無いポケモン・7 体以上・使用中のチームが無いなどは 400。
//   DELETE /api/my/access-key  → 204（アクセスキーの作り直しで、古いキーを使えなくしてその置き場を消す。以後そのキーは 410）
//
// revision は登録データ全体で 1 つ（どちらの PUT でも増える）。
// 認証は "Authorization: Bearer <アクセスキー>"。アクセスキーは 256 bit の乱数を base64url（パディングなし・43 文字）にしたもの。
// サーバーはキーそのものを保存しない。SHA-256(キー) の 16 進を Durable Object の名前にして、キーごとに 1 つの置き場を持つ。
// キーはログ・エラーメッセージ・URL に出さない（このファイルでは console.* を使わない）。

import { DurableObject } from "cloudflare:workers";

/** パス → 使えるメソッド。 */
const ROUTES = {
  "/api/my/pokemon": ["GET", "PUT"],
  "/api/my/team": ["GET", "PUT"],
  "/api/my/access-key": ["DELETE"],
};
const KEY_PATTERN = /^[A-Za-z0-9_-]{43}$/;
/** ゲーム（データセット）のキー。 */
const DATA_SET_PATTERN = /^[A-Za-z0-9_-]{1,32}$/;
/** PUT の本文と、保存する登録データ全体の上限（UTF-8 のバイト数）。 */
const MAX_BYTES = 256 * 1024;
/** 1 チームの上限。 */
const MAX_TEAM_SIZE = 6;

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

function tooLarge() {
  return error(413, "too_large", `データが大きすぎます（上限 ${MAX_BYTES / 1024} KB）`);
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

function isId(v) {
  return Number.isSafeInteger(v) && v >= 1;
}

/**
 * 検証の失敗（400）。Durable Object の呼び出しをまたぐと例外の型が分からなくなるので、
 * 投げた側（同じ実行環境の中）で {@link catchInvalid} によって 400 の応答に直す。
 */
class InvalidError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}

function invalid(code, message) {
  throw new InvalidError(code, message);
}

/** InvalidError を 400 の応答に直す。 */
async function catchInvalid(action) {
  try {
    return await action();
  } catch (e) {
    if (e instanceof InvalidError) return error(400, e.code, e.message);
    throw e;
  }
}

/** PUT の本文を検証して {baseRevision, dataSets} を返す（dataSets の中身は各リソースの検証で見る）。 */
function parsePutBody(bytes) {
  let body;
  try {
    body = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  } catch {
    invalid("invalid_json", "本文が JSON ではありません");
  }
  if (!isPlainObject(body)) invalid("invalid_body", "本文はオブジェクトにしてください");
  const { baseRevision, dataSets } = body;
  if (!Number.isSafeInteger(baseRevision) || baseRevision < 0)
    invalid("invalid_revision", "baseRevision は 0 以上の整数にしてください");
  if (!isPlainObject(dataSets)) invalid("invalid_data_sets", "dataSets はゲームのキーごとのオブジェクトにしてください");
  const keys = Object.keys(dataSets);
  if (keys.length === 0) invalid("invalid_data_sets", "dataSets に 1 つ以上のゲームを入れてください");
  const seen = new Set();
  for (const key of keys) {
    if (!DATA_SET_PATTERN.test(key)) invalid("invalid_data_set", "ゲームのキーは英数字・-・_（32 文字まで）にしてください");
    const lower = key.toLowerCase();
    if (seen.has(lower)) invalid("invalid_data_set", `同じゲームが 2 回あります: ${key}`);
    seen.add(lower);
    if (!isPlainObject(dataSets[key])) invalid("invalid_data_set", `${key} はオブジェクトにしてください`);
  }
  return { baseRevision, dataSets };
}

/** my-pokemon の 1 ゲーム分を検証して {pokemon, nextId} を返す。 */
function validatePokemons(key, value) {
  const { pokemon, nextId } = value;
  if (!Array.isArray(pokemon)) invalid("invalid_pokemon", `${key}.pokemon は配列にしてください`);
  if (nextId !== undefined && nextId !== null && !isId(nextId))
    invalid("invalid_next_id", `${key}.nextId は 1 以上の整数にしてください`);
  const ids = new Set();
  for (const p of pokemon) {
    if (!isPlainObject(p)) invalid("invalid_pokemon", `${key}.pokemon の要素はオブジェクトにしてください`);
    if (!isId(p.Id)) invalid("invalid_pokemon", `${key}.pokemon の Id は 1 以上の整数にしてください`);
    if (ids.has(p.Id)) invalid("duplicate_pokemon", `${key}.pokemon に同じ Id（${p.Id}）が 2 つあります`);
    ids.add(p.Id);
  }
  return { pokemon, nextId: nextId ?? null };
}

/** my-team の 1 ゲーム分を、そのゲームの登録ポケモン（ID の集合）と突き合わせて検証する。 */
function validateTeams(key, value, pokemonIds) {
  const { teams, activeTeam } = value;
  if (!Array.isArray(teams)) invalid("invalid_teams", `${key}.teams は配列にしてください`);
  const numbers = new Set();
  for (const t of teams) {
    if (!isPlainObject(t)) invalid("invalid_team", `${key}.teams の要素はオブジェクトにしてください`);
    if (!isId(t.Number)) invalid("invalid_team", `${key}.teams の Number は 1 以上の整数にしてください`);
    if (numbers.has(t.Number)) invalid("duplicate_team", `${key} にチーム${t.Number}が 2 つあります`);
    numbers.add(t.Number);
    if (!Array.isArray(t.Slots)) invalid("invalid_team", `${key} のチーム${t.Number}の Slots は配列にしてください`);
    if (t.Slots.length === 0) invalid("empty_team", `${key} のチーム${t.Number}が 0 体です（空のチームは送らずに消してください）`);
    if (t.Slots.length > MAX_TEAM_SIZE)
      invalid("team_too_large", `${key} のチーム${t.Number}が ${t.Slots.length} 体います（${MAX_TEAM_SIZE} 体まで）`);
    const entries = new Set();
    for (const s of t.Slots) {
      if (!isPlainObject(s) || !isId(s.EntryId))
        invalid("invalid_slot", `${key} のチーム${t.Number}の要素は EntryId（1 以上の整数）を持つオブジェクトにしてください`);
      if (s.ItemId !== undefined && s.ItemId !== null && !isId(s.ItemId))
        invalid("invalid_slot", `${key} のチーム${t.Number}の ItemId は 1 以上の整数か null にしてください`);
      if (!pokemonIds.has(s.EntryId))
        invalid("unknown_pokemon", `${key} のチーム${t.Number}に、登録に無いポケモン（Id ${s.EntryId}）が入っています`);
      if (entries.has(s.EntryId)) invalid("duplicate_slot", `${key} のチーム${t.Number}に同じポケモン（Id ${s.EntryId}）が 2 回入っています`);
      entries.add(s.EntryId);
    }
  }
  if (activeTeam !== undefined && activeTeam !== null) {
    if (!isId(activeTeam)) invalid("invalid_active_team", `${key}.activeTeam はチーム番号か null にしてください`);
    if (!numbers.has(activeTeam)) invalid("unknown_active_team", `${key}.activeTeam のチーム${activeTeam}がありません`);
  }
  return { teams, activeTeam: activeTeam ?? null };
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (!url.pathname.startsWith("/api/") || url.pathname.startsWith("/api/game/")) {
      // run_worker_first の対象外（通常は来ない。/api/game/* はゲームデータの静的アセット）。静的アセットに任せる。
      // ここを通ったことが分かるようにヘッダーを付ける（tools/worker-api-test.mjs が「付いていない」ことを確かめる）
      const response = await env.ASSETS.fetch(request);
      const passed = new Response(response.body, response);
      passed.headers.set("X-Worker-Fallback", "assets");
      return passed;
    }
    try {
      return await catchInvalid(() => handleApi(request, env, url));
    } catch {
      // 詳細（キーを含む可能性がある）は返さない
      return error(500, "internal_error", "サーバーでエラーが発生しました");
    }
  },
};

async function handleApi(request, env, url) {
  const methods = ROUTES[url.pathname];
  if (!methods) return error(404, "not_found", "見つかりません");
  const method = request.method;
  if (!methods.includes(method)) return error(405, "method_not_allowed", "使えないメソッドです", { Allow: methods.join(", ") });

  const key = readKey(request);
  if (key === null)
    return error(401, "unauthorized", "アクセスキーが正しくありません", { "WWW-Authenticate": 'Bearer realm="pokemon"' });

  // Durable Object にはキーを渡さない（名前は SHA-256 のみ）
  const stub = env.ROSTER.get(env.ROSTER.idFromName(await sha256Hex(key)));

  if (url.pathname === "/api/my/access-key") return stub.retire();
  const resource = url.pathname === "/api/my/pokemon" ? "pokemon" : "teams";
  if (method === "GET") return stub.getResource(resource);

  let bytes;
  try {
    bytes = await readBodyLimited(request, MAX_BYTES);
  } catch (e) {
    if (e instanceof TooLargeError) return tooLarge();
    throw e;
  }
  const { baseRevision, dataSets } = parsePutBody(bytes);
  if (resource === "pokemon") {
    const values = {};
    for (const [k, v] of Object.entries(dataSets)) values[k] = validatePokemons(k, v);
    return stub.putPokemons(baseRevision, values);
  }
  // チームは登録ポケモンとの突き合わせが要るので、Durable Object の中で検証する
  return stub.putTeams(baseRevision, dataSets);
}

/** 保存している登録データ（RosterState の JSON）から、ゲームのキーを大文字小文字を区別せずに探す。 */
function findDataSetKey(roster, key) {
  const lower = key.toLowerCase();
  return Object.keys(roster.DataSets).find((k) => k.toLowerCase() === lower) ?? key;
}

/** ゲーム 1 つ分（DataSetRoster）を、項目が欠けていても扱える形にする（知らない項目は残す）。 */
function normalizeDataSet(d) {
  const r = isPlainObject(d) ? d : {};
  if (!Array.isArray(r.Pokemon)) r.Pokemon = [];
  if (!Array.isArray(r.Teams)) r.Teams = [];
  if (!isId(r.NextId)) r.NextId = 1;
  if (r.ActiveTeam === undefined) r.ActiveTeam = null;
  return r;
}

/**
 * アクセスキー 1 つ分の置き場（SQLite ストレージの Durable Object）。
 * 名前ごとにインスタンスは 1 つだけで、リクエストは 1 つずつ処理されるので、revision の比較と保存の間に別の書き込みは入らない
 * （PUT の検証・連鎖する変更・保存は 1 回の呼び出しの中で行い、保存は 1 回の put で書く）。
 *
 * ストレージ: "revision"（数値）・"roster"（RosterState の JSON 文字列）・"deleted"（キーの作り直しで消した印）
 * 登録データは今までどおり 1 つの文書として保存し、API はその一部（ポケモン / チーム）を読み書きする。
 */
export class RosterSlot extends DurableObject {
  async #read() {
    const values = await this.ctx.storage.get(["revision", "roster", "deleted"]);
    const text = values.get("roster");
    let roster = text ? JSON.parse(text) : null;
    if (!isPlainObject(roster)) roster = { Version: 1, DataSets: {} };
    if (!isPlainObject(roster.DataSets)) roster.DataSets = {};
    return {
      revision: values.get("revision") ?? 0,
      roster,
      deleted: values.get("deleted") === true,
    };
  }

  #gone() {
    return error(410, "key_retired", "このアクセスキーは作り直されたため使えません。新しいアクセスキーを使ってください");
  }

  /** GET・409 の本文（リソースの現在の内容）。 */
  #view(resource, revision, roster) {
    const dataSets = {};
    for (const [k, v] of Object.entries(roster.DataSets)) {
      const d = normalizeDataSet(v);
      dataSets[k] = resource === "pokemon"
        ? { nextId: d.NextId, pokemon: d.Pokemon }
        : { teams: d.Teams, activeTeam: d.ActiveTeam };
    }
    return { revision, dataSets };
  }

  async #save(revision, roster) {
    const text = JSON.stringify(roster);
    if (new TextEncoder().encode(text).byteLength > MAX_BYTES) return tooLarge();
    const next = revision + 1;
    await this.ctx.storage.put({ revision: next, roster: text });
    return json(200, { revision: next });
  }

  async getResource(resource) {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    return json(200, this.#view(resource, s.revision, s.roster));
  }

  /** ポケモンを置き換え、消えたポケモンをチームから外す（空のチームは消し、使用中が消えたら解除）。 */
  async putPokemons(baseRevision, values) {
    const s = await this.#read();
    if (s.deleted) return this.#gone();
    if (baseRevision !== s.revision) return json(409, this.#view("pokemon", s.revision, s.roster));
    for (const [requested, { pokemon, nextId }] of Object.entries(values)) {
      const key = findDataSetKey(s.roster, requested);
      const d = normalizeDataSet(s.roster.DataSets[key]);
      s.roster.DataSets[key] = d;
      d.Pokemon = pokemon;
      // 登録 ID は使い回さない（消した ID の次から振る）
      const maxId = pokemon.reduce((m, p) => Math.max(m, p.Id), 0);
      d.NextId = Math.max(d.NextId, nextId ?? 1, maxId + 1);
      const ids = new Set(pokemon.map((p) => p.Id));
      const teams = [];
      for (const t of d.Teams) {
        if (!isPlainObject(t) || !Array.isArray(t.Slots)) continue;
        t.Slots = t.Slots.filter((slot) => isPlainObject(slot) && ids.has(slot.EntryId));
        if (t.Slots.length > 0) teams.push(t);
      }
      d.Teams = teams;
      if (d.ActiveTeam !== null && !teams.some((t) => t.Number === d.ActiveTeam)) d.ActiveTeam = null;
    }
    return this.#save(s.revision, s.roster);
  }

  /** チームと使用中チームを置き換える（登録に無いポケモンを含むなどは 400 で、何も保存しない）。 */
  async putTeams(baseRevision, dataSets) {
    return catchInvalid(async () => {
      const s = await this.#read();
      if (s.deleted) return this.#gone();
      if (baseRevision !== s.revision) return json(409, this.#view("teams", s.revision, s.roster));
      const updates = [];
      for (const [requested, value] of Object.entries(dataSets)) {
        const key = findDataSetKey(s.roster, requested);
        const d = normalizeDataSet(s.roster.DataSets[key]);
        const ids = new Set(d.Pokemon.filter(isPlainObject).map((p) => p.Id));
        updates.push([key, d, validateTeams(requested, value, ids)]);
      }
      for (const [key, d, { teams, activeTeam }] of updates) {
        d.Teams = [...teams].sort((a, b) => a.Number - b.Number);
        d.ActiveTeam = activeTeam;
        s.roster.DataSets[key] = d;
      }
      return this.#save(s.revision, s.roster);
    });
  }

  /** アクセスキーを使えなくし、置き場のデータを消す。 */
  async retire() {
    await this.ctx.storage.deleteAll();
    await this.ctx.storage.put("deleted", true);
    return json(204);
  }
}
