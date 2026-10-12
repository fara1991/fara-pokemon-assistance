// データ連携 API（worker/index.js）の動作確認。
//   npm run test:worker
// wrangler dev をローカル（一時ディレクトリに保存）で起動して、401・空の GET・PUT・409・413・DELETE などを確かめる。
// publish/wwwroot が無いと wrangler dev が起動しないので、無ければ空の index.html だけ置く。
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const port = Number(process.env.PORT ?? 8799);
const base = `http://127.0.0.1:${port}`;
const persist = mkdtempSync(join(tmpdir(), "roster-api-test-"));

if (!existsSync("publish/wwwroot/index.html")) {
  mkdirSync("publish/wwwroot", { recursive: true });
  writeFileSync("publish/wwwroot/index.html", "<!doctype html><title>test</title>");
}

const wrangler = spawn(
  process.platform === "win32" ? "npx.cmd" : "npx",
  ["wrangler", "dev", "--port", String(port), "--ip", "127.0.0.1", "--persist-to", persist, "--show-interactive-dev-session=false"],
  { stdio: ["ignore", "pipe", "pipe"], shell: process.platform === "win32", detached: process.platform !== "win32" },
);
let output = "";
wrangler.stdout.on("data", (d) => (output += d));
wrangler.stderr.on("data", (d) => (output += d));

const newKey = () => randomBytes(32).toString("base64url");
const auth = (key) => ({ Authorization: `Bearer ${key}` });
let failures = 0;
function check(name, ok, detail = "") {
  console.log(`${ok ? "OK  " : "FAIL"} ${name}${ok || !detail ? "" : ` — ${detail}`}`);
  if (!ok) failures++;
}

async function waitReady() {
  for (let i = 0; i < 120; i++) {
    try {
      const r = await fetch(`${base}/api/roster`);
      if (r.status === 401) return;
    } catch {}
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error("wrangler dev が起動しませんでした\n" + output);
}

async function req(method, key, body, headers = {}) {
  const r = await fetch(`${base}/api/roster`, {
    method,
    headers: { ...(key ? auth(key) : {}), ...(body !== undefined ? { "Content-Type": "application/json" } : {}), ...headers },
    body: body === undefined ? undefined : typeof body === "string" ? body : JSON.stringify(body),
  });
  const text = await r.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch {}
  return { status: r.status, json, text, headers: r.headers };
}

try {
  await waitReady();
  const key = newKey();
  const roster = { Version: 1, DataSets: { Champions: { NextId: 2, Pokemon: [{ Id: 1, PokemonName: "ガブリアス" }], Teams: [], ActiveTeam: null } } };

  // 認証
  check("キーなしは 401", (await req("GET")).status === 401);
  check("短いキーは 401", (await req("GET", "abc")).status === 401);
  check("44 文字のキーは 401", (await req("GET", key + "A")).status === 401);
  check("使えない文字を含むキーは 401", (await req("GET", key.slice(0, 42) + "=")).status === 401);
  check("Bearer 以外は 401", (await req("GET", null, undefined, { Authorization: `Basic ${key}` })).status === 401);
  const unauth = await req("PUT", null, { baseRevision: 0, roster });
  check("キーなしの PUT は 401", unauth.status === 401);
  check("401 の本文にキーが含まれない", !unauth.text.includes(key));

  // 空の GET
  const empty = await req("GET", key);
  check("空の GET は 200 / revision 0 / roster null", empty.status === 200 && empty.json?.revision === 0 && empty.json?.roster === null, empty.text);
  check("Cache-Control: no-store", empty.headers.get("cache-control") === "no-store");
  check("CORS ヘッダーを付けない", empty.headers.get("access-control-allow-origin") === null);

  // PUT → GET
  const put1 = await req("PUT", key, { baseRevision: 0, roster });
  check("PUT（baseRevision 0）は 200 / revision 1", put1.status === 200 && put1.json?.revision === 1, put1.text);
  const got = await req("GET", key);
  check("GET で保存した内容が返る", got.status === 200 && got.json?.revision === 1 && JSON.stringify(got.json?.roster) === JSON.stringify(roster), got.text);

  // 別のキーからは見えない
  const other = await req("GET", newKey());
  check("別のキーは空", other.status === 200 && other.json?.roster === null && other.json?.revision === 0);

  // 競合
  const stale = await req("PUT", key, { baseRevision: 0, roster: { Version: 1, DataSets: {} } });
  check("古い baseRevision の PUT は 409 で現在の内容を返す", stale.status === 409 && stale.json?.revision === 1 && JSON.stringify(stale.json?.roster) === JSON.stringify(roster), stale.text);
  const put2 = await req("PUT", key, { baseRevision: 1, roster: { Version: 1, DataSets: {} } });
  check("最新の baseRevision の PUT は 200 / revision 2", put2.status === 200 && put2.json?.revision === 2, put2.text);

  // 本文の検証
  check("JSON でない本文は 400", (await req("PUT", key, "{not json")).status === 400);
  check("roster が配列なら 400", (await req("PUT", key, { baseRevision: 2, roster: [] })).status === 400);
  check("roster が null なら 400", (await req("PUT", key, { baseRevision: 2, roster: null })).status === 400);
  check("baseRevision が無ければ 400", (await req("PUT", key, { roster })).status === 400);
  check("baseRevision が負なら 400", (await req("PUT", key, { baseRevision: -1, roster })).status === 400);
  check("baseRevision が小数なら 400", (await req("PUT", key, { baseRevision: 1.5, roster })).status === 400);
  const big = { baseRevision: 2, roster: { pad: "あ".repeat(90 * 1024) } }; // 約 270 KB（UTF-8）
  check("256 KB を超える本文は 413", (await req("PUT", key, big)).status === 413);
  const afterBad = await req("GET", key);
  check("不正な PUT では内容が変わらない", afterBad.json?.revision === 2, afterBad.text);

  // その他のパス・メソッド
  check("POST は 405", (await req("POST", key, {})).status === 405);
  const notFound = await fetch(`${base}/api/other`, { headers: auth(key) });
  check("/api/ の他のパスは 404", notFound.status === 404);
  const page = await fetch(`${base}/roster`);
  check("API 以外は今までどおり静的アセット（SPA）", page.status === 200 && (page.headers.get("content-type") ?? "").includes("text/html"));

  // DELETE（キーの作り直し）
  const del = await req("DELETE", key);
  check("DELETE は 204", del.status === 204);
  check("DELETE 後の GET は 410", (await req("GET", key)).status === 410);
  check("DELETE 後の PUT は 410", (await req("PUT", key, { baseRevision: 0, roster })).status === 410);
  check("DELETE はもう一度呼んでも 204", (await req("DELETE", key)).status === 204);
  check("キーなしの DELETE は 401", (await req("DELETE")).status === 401);
} catch (e) {
  console.error(e);
  failures++;
} finally {
  // npx の子プロセス（workerd）ごと止める
  try {
    if (process.platform === "win32") spawn("taskkill", ["/pid", String(wrangler.pid), "/T", "/F"]);
    else process.kill(-wrangler.pid, "SIGTERM");
  } catch {}
  await new Promise((r) => setTimeout(r, 500));
  rmSync(persist, { recursive: true, force: true });
}

console.log(failures === 0 ? "\nすべて OK" : `\n${failures} 件失敗`);
process.exit(failures === 0 ? 0 : 1);
