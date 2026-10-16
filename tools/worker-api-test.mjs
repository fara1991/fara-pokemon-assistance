// データ共有 API（worker/index.js）の動作確認。
//   npm run test:worker
// wrangler dev をローカル（一時ディレクトリに保存）で起動して、/api/my/pokemon・/api/my/team・/api/my/access-key の
// 401・空の GET・PUT・409・400・413・410、ポケモン削除でチームから外れることなどを確かめる。
// あわせて、ゲームデータ（/api/game/*）が Worker を通らずに静的アセットとして返ること、以前のパス（/api/my-pokemon・/data/...）が無いことも確かめる。
// publish/wwwroot が無いと wrangler dev が起動しないので、無ければ空の index.html と最小の api/game/datasets.csv だけ置く。
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
if (!existsSync("publish/wwwroot/api/game/datasets.csv")) {
  mkdirSync("publish/wwwroot/api/game", { recursive: true });
  writeFileSync("publish/wwwroot/api/game/datasets.csv", "Key,Name,Generation,EvSystem,CommandPrefix,Gimmick,UsageFallback,Calc\nChampions,test,9,Points,pokech,None,,Full\n");
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
      const r = await fetch(`${base}/api/my/pokemon`);
      if (r.status === 401) return;
    } catch {}
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error("wrangler dev が起動しませんでした\n" + output);
}

async function req(method, path, key, body, headers = {}) {
  const r = await fetch(`${base}${path}`, {
    method,
    headers: { ...(key ? auth(key) : {}), ...(body !== undefined ? { "Content-Type": "application/json" } : {}), ...headers },
    body: body === undefined ? undefined : typeof body === "string" ? body : JSON.stringify(body),
  });
  const text = await r.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch {}
  return { status: r.status, json, text, headers: r.headers };
}
const P = "/api/my/pokemon";
const T = "/api/my/team";
const K = "/api/my/access-key";
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const mon = (id, name) => ({ Id: id, PokemonId: 445, PokemonName: name, HP: 4, Attack: 32, Nature: "ようき" });
const slot = (id, item = null) => ({ EntryId: id, ItemId: item });

try {
  await waitReady();
  const key = newKey();

  // 認証
  check("キーなしは 401", (await req("GET", P)).status === 401);
  check("キーなしの my-team は 401", (await req("GET", T)).status === 401);
  check("短いキーは 401", (await req("GET", P, "abc")).status === 401);
  check("44 文字のキーは 401", (await req("GET", P, key + "A")).status === 401);
  check("使えない文字を含むキーは 401", (await req("GET", P, key.slice(0, 42) + "=")).status === 401);
  check("Bearer 以外は 401", (await req("GET", P, null, undefined, { Authorization: `Basic ${key}` })).status === 401);
  const unauth = await req("PUT", P, null, { baseRevision: 0, dataSets: { Champions: { pokemon: [] } } });
  check("キーなしの PUT は 401", unauth.status === 401);
  check("401 の本文にキーが含まれない", !unauth.text.includes(key));
  check("キーなしの DELETE /api/my/access-key は 401", (await req("DELETE", K)).status === 401);

  // 空の GET
  const emptyP = await req("GET", P, key);
  check("空の GET my-pokemon は 200 / revision 0 / dataSets {}", emptyP.status === 200 && emptyP.json?.revision === 0 && same(emptyP.json?.dataSets, {}), emptyP.text);
  const emptyT = await req("GET", T, key);
  check("空の GET my-team は 200 / revision 0 / dataSets {}", emptyT.status === 200 && emptyT.json?.revision === 0 && same(emptyT.json?.dataSets, {}), emptyT.text);
  check("Cache-Control: no-store", emptyP.headers.get("cache-control") === "no-store");
  check("CORS ヘッダーを付けない", emptyP.headers.get("access-control-allow-origin") === null);

  // ポケモンの PUT → GET
  const mons = [mon(1, "ガブリアス"), mon(2, "リザードン"), mon(3, "イエッサン(♂)")];
  const put1 = await req("PUT", P, key, { baseRevision: 0, dataSets: { Champions: { pokemon: mons } } });
  check("PUT my-pokemon（baseRevision 0）は 200 / revision 1", put1.status === 200 && put1.json?.revision === 1, put1.text);
  const got = await req("GET", P, key);
  check("GET my-pokemon で保存した内容と nextId が返る",
    got.json?.revision === 1 && same(got.json?.dataSets?.Champions?.pokemon, mons) && got.json?.dataSets?.Champions?.nextId === 4, got.text);

  // チームの PUT → GET（revision は共通）
  const teams = [{ Number: 1, Slots: [slot(1, 275), slot(2)] }, { Number: 2, Slots: [slot(2)] }, { Number: 3, Slots: [slot(3)] }];
  const put2 = await req("PUT", T, key, { baseRevision: 1, dataSets: { champions: { teams, activeTeam: 2 } } });
  check("PUT my-team は 200 / revision 2（ゲームのキーは大文字小文字を区別しない）", put2.status === 200 && put2.json?.revision === 2, put2.text);
  const gotT = await req("GET", T, key);
  check("GET my-team で保存した内容が返る", gotT.json?.revision === 2 && same(gotT.json?.dataSets?.Champions?.teams, teams) && gotT.json?.dataSets?.Champions?.activeTeam === 2 && gotT.json?.dataSets?.champions === undefined, gotT.text);
  check("GET my-pokemon も revision 2", (await req("GET", P, key)).json?.revision === 2);

  // 別のキーからは見えない
  const other = await req("GET", P, newKey());
  check("別のキーは空", other.status === 200 && other.json?.revision === 0 && same(other.json?.dataSets, {}));

  // 競合（409 は現在の内容を返す）
  const staleP = await req("PUT", P, key, { baseRevision: 1, dataSets: { Champions: { pokemon: [] } } });
  check("古い baseRevision の PUT my-pokemon は 409 で現在の内容を返す", staleP.status === 409 && staleP.json?.revision === 2 && same(staleP.json?.dataSets?.Champions?.pokemon, mons), staleP.text);
  const staleT = await req("PUT", T, key, { baseRevision: 0, dataSets: { Champions: { teams: [], activeTeam: null } } });
  check("古い baseRevision の PUT my-team は 409 で現在の内容を返す", staleT.status === 409 && staleT.json?.revision === 2 && same(staleT.json?.dataSets?.Champions?.teams, teams), staleT.text);

  // チームの検証（400・何も変わらない）
  const badTeam = async (name, dataSets) => {
    const r = await req("PUT", T, key, { baseRevision: 2, dataSets });
    check(`${name} は 400`, r.status === 400 && typeof r.json?.message === "string", r.text);
  };
  await badTeam("登録に無いポケモン ID", { Champions: { teams: [{ Number: 1, Slots: [slot(99)] }], activeTeam: null } });
  await badTeam("7 体のチーム", { Champions: { teams: [{ Number: 1, Slots: [1, 2, 3, 4, 5, 6, 7].map((i) => slot(i)) }], activeTeam: null } });
  await badTeam("存在しない使用中チーム", { Champions: { teams: [{ Number: 1, Slots: [slot(1)] }], activeTeam: 5 } });
  await badTeam("同じチーム番号が 2 つ", { Champions: { teams: [{ Number: 1, Slots: [slot(1)] }, { Number: 1, Slots: [slot(2)] }], activeTeam: null } });
  await badTeam("同じポケモンが 2 回", { Champions: { teams: [{ Number: 1, Slots: [slot(1), slot(1)] }], activeTeam: null } });
  await badTeam("空のチーム", { Champions: { teams: [{ Number: 1, Slots: [] }], activeTeam: null } });
  await badTeam("登録の無いゲームのチーム", { Gen9: { teams: [{ Number: 1, Slots: [slot(1)] }], activeTeam: null } });
  const r400 = await req("PUT", T, key, { baseRevision: 2, dataSets: { Champions: { teams: [{ Number: 1, Slots: [slot(99)] }], activeTeam: null } } });
  check("400 のメッセージに理由（登録に無いポケモン）", (r400.json?.message ?? "").includes("登録に無い"), r400.text);

  // 本文の検証
  check("JSON でない本文は 400", (await req("PUT", P, key, "{not json")).status === 400);
  check("dataSets が無ければ 400", (await req("PUT", P, key, { baseRevision: 2 })).status === 400);
  check("dataSets が配列なら 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: [] })).status === 400);
  check("dataSets が空なら 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: {} })).status === 400);
  check("ゲームのキーが不正なら 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: { "a/b": { pokemon: [] } } })).status === 400);
  check("pokemon が配列でなければ 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: { Champions: { pokemon: {} } } })).status === 400);
  check("Id の無いポケモンは 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: { Champions: { pokemon: [{ PokemonName: "x" }] } } })).status === 400);
  check("同じ Id のポケモンは 400", (await req("PUT", P, key, { baseRevision: 2, dataSets: { Champions: { pokemon: [mon(1, "a"), mon(1, "b")] } } })).status === 400);
  check("baseRevision が無ければ 400", (await req("PUT", P, key, { dataSets: { Champions: { pokemon: [] } } })).status === 400);
  check("baseRevision が負なら 400", (await req("PUT", P, key, { baseRevision: -1, dataSets: { Champions: { pokemon: [] } } })).status === 400);
  check("baseRevision が小数なら 400", (await req("PUT", P, key, { baseRevision: 1.5, dataSets: { Champions: { pokemon: [] } } })).status === 400);
  const big = { baseRevision: 2, dataSets: { Champions: { pokemon: [{ Id: 1, pad: "あ".repeat(90 * 1024) }] } } }; // 約 270 KB（UTF-8）
  check("256 KB を超える本文は 413", (await req("PUT", P, key, big)).status === 413);
  const afterBad = await req("GET", T, key);
  check("不正な PUT では内容が変わらない", afterBad.json?.revision === 2 && same(afterBad.json?.dataSets?.Champions?.teams, teams), afterBad.text);

  // 保存後の全体が 256 KB を超えるなら 413（本文はそれぞれ上限内）
  const sizeKey = newKey();
  const half = (id) => [{ Id: id, pad: "x".repeat(150 * 1024) }];
  check("1 つめのゲームは保存できる", (await req("PUT", P, sizeKey, { baseRevision: 0, dataSets: { Gen8: { pokemon: half(1) } } })).status === 200);
  check("合計が 256 KB を超える PUT は 413", (await req("PUT", P, sizeKey, { baseRevision: 1, dataSets: { Gen9: { pokemon: half(1) } } })).status === 413);
  check("413 のあとも revision は変わらない", (await req("GET", P, sizeKey)).json?.revision === 1);

  // ポケモンの削除でチームから外れる・空のチームが消える・使用中が解除される（同じ PUT の中で）
  const put3 = await req("PUT", P, key, { baseRevision: 2, dataSets: { Champions: { pokemon: [mons[0], mons[2]] } } });
  check("ポケモンを消す PUT は 200 / revision 3", put3.status === 200 && put3.json?.revision === 3, put3.text);
  const afterRemove = await req("GET", T, key);
  const ch = afterRemove.json?.dataSets?.Champions;
  check("消したポケモンがチームから外れる", same(ch?.teams?.find((t) => t.Number === 1)?.Slots, [slot(1, 275)]), afterRemove.text);
  check("0 体になったチームが消える", ch?.teams?.length === 2 && !ch.teams.some((t) => t.Number === 2), afterRemove.text);
  check("使用中のチームが消えたら使用中を解除", ch?.activeTeam === null, afterRemove.text);
  check("チームの変更も同じ revision 3", afterRemove.json?.revision === 3);
  const afterRemoveP = await req("GET", P, key);
  check("消した ID は使い回さない（nextId は 4 のまま）", afterRemoveP.json?.dataSets?.Champions?.nextId === 4, afterRemoveP.text);

  // 書かなかったゲームはそのまま
  const put4 = await req("PUT", P, key, { baseRevision: 3, dataSets: { Gen9: { pokemon: [mon(1, "ハバタクカミ")] } } });
  check("別のゲームの PUT は 200 / revision 4", put4.status === 200 && put4.json?.revision === 4, put4.text);
  const both = await req("GET", P, key);
  check("書かなかったゲームのポケモンは変わらない", both.json?.dataSets?.Champions?.pokemon?.length === 2 && both.json?.dataSets?.Gen9?.pokemon?.length === 1, both.text);
  const bothT = await req("GET", T, key);
  check("書かなかったゲームのチームは変わらない", bothT.json?.dataSets?.Champions?.teams?.length === 2 && same(bothT.json?.dataSets?.Gen9?.teams, []), bothT.text);

  // 相手のポケモン（/api/my/rival）: 対戦中の相手と試合の履歴
  const rkey = newKey();
  const R = "/api/my/rival";
  check("キーなしの my-rival は 401", (await req("GET", R)).status === 401);
  const emptyR = await req("GET", R, rkey);
  check("空の GET my-rival は 200 / revision 0 / dataSets {}", emptyR.status === 200 && emptyR.json?.revision === 0 && same(emptyR.json?.dataSets, {}), emptyR.text);
  const rival = (id, name, extra = {}) => ({ Id: id, PokemonId: 445, PokemonName: name, ...extra });
  const match = (id, foes) => ({ Id: id, Date: "2026-10-16T10:00:00+09:00", Memo: "テスト戦", Foes: foes });
  const rivals1 = [rival(1, "カイリュー", { SpDefense: 32 }), rival(2, "ハバタクカミ")];
  const putR1 = await req("PUT", R, rkey, { baseRevision: 0, dataSets: { Champions: { rivals: rivals1, history: [] } } });
  check("PUT my-rival は 200 / revision 1", putR1.status === 200 && putR1.json?.revision === 1, putR1.text);
  const gotR = await req("GET", R, rkey);
  check("GET my-rival で保存した内容と nextId が返る",
    gotR.json?.revision === 1 && same(gotR.json?.dataSets?.Champions?.rivals, rivals1) && gotR.json?.dataSets?.Champions?.nextId === 3 && gotR.json?.dataSets?.Champions?.nextMatchId === 1, gotR.text);
  const history1 = [match(1, [rivals1[0]])];
  const putR2 = await req("PUT", R, rkey, { baseRevision: 1, dataSets: { Champions: { rivals: [], nextId: 3, history: history1 } } });
  check("試合を終えて履歴に移す PUT は 200 / revision 2", putR2.status === 200 && putR2.json?.revision === 2, putR2.text);
  const gotR2 = await req("GET", R, rkey);
  check("履歴が保存され、相手は空になり、ID は使い回さない（nextId 3・nextMatchId 2）",
    same(gotR2.json?.dataSets?.Champions?.history, history1) && gotR2.json?.dataSets?.Champions?.rivals?.length === 0 && gotR2.json?.dataSets?.Champions?.nextId === 3 && gotR2.json?.dataSets?.Champions?.nextMatchId === 2, gotR2.text);
  check("my-pokemon の revision も 2（登録データ全体で 1 つ）", (await req("GET", P, rkey)).json?.revision === 2);
  const staleR = await req("PUT", R, rkey, { baseRevision: 1, dataSets: { Champions: { rivals: [], history: [] } } });
  check("古い baseRevision の PUT my-rival は 409 で現在の内容を返す", staleR.status === 409 && staleR.json?.revision === 2 && same(staleR.json?.dataSets?.Champions?.history, history1), staleR.text);
  const badR = (name, value) => req("PUT", R, rkey, { baseRevision: 2, dataSets: { Champions: value } }).then((r) => check(`${name} は 400`, r.status === 400, r.text));
  await badR("rivals が配列でない", { rivals: {}, history: [] });
  await badR("history が配列でない", { rivals: [], history: {} });
  await badR("Id の無い相手", { rivals: [{ PokemonName: "x" }], history: [] });
  await badR("同じ Id の相手", { rivals: [rival(1, "a"), rival(1, "b")], history: [] });
  await badR("Foes が配列でない履歴", { rivals: [], history: [{ Id: 1, Foes: {} }] });
  await badR("同じ Id の履歴", { rivals: [], history: [match(1, []), match(1, [])] });
  check("不正な PUT では my-rival の内容が変わらない", (await req("GET", R, rkey)).json?.revision === 2);
  check("my-pokemon の PUT は相手のポケモンを消さない", (await req("PUT", P, rkey, { baseRevision: 2, dataSets: { Champions: { pokemon: [mon(1, "ガブリアス")] } } })).status === 200
    && same((await req("GET", R, rkey)).json?.dataSets?.Champions?.history, history1));
  check("DELETE /api/my/rival は 405", (await req("DELETE", R, rkey)).status === 405);

  // その他のパス・メソッド
  check("POST は 405", (await req("POST", P, key, {})).status === 405);
  check("DELETE /api/my/pokemon は 405", (await req("DELETE", P, key)).status === 405);
  check("GET /api/my/access-key は 405", (await req("GET", K, key)).status === 405);
  check("以前の /api/roster は 404", (await req("GET", "/api/roster", key)).status === 404);
  check("以前の /api/my-pokemon は 404", (await req("GET", "/api/my-pokemon", key)).status === 404);
  check("以前の /api/my-team は 404", (await req("GET", "/api/my-team", key)).status === 404);
  check("以前の DELETE /api/access-key は 404", (await req("DELETE", "/api/access-key", key)).status === 404);
  check("複数形の /api/my/pokemons は 404", (await req("GET", "/api/my/pokemons", key)).status === 404);
  check("/api/my そのものは 404", (await req("GET", "/api/my", key)).status === 404);
  check("/api/ の他のパスは 404", (await req("GET", "/api/other", key)).status === 404);
  const page = await fetch(`${base}/my-pokemon`);
  check("以前の URL（/my-teams）も静的アセット（SPA）", (await fetch(`${base}/my-teams`)).status === 200);
  check("API 以外は今までどおり静的アセット（SPA）", page.status === 200 && (page.headers.get("content-type") ?? "").includes("text/html"));

  // ゲームデータ（/api/game/*）は静的アセット。run_worker_first の "!/api/game/*" で Worker を通らない
  // （Worker を通ると worker/index.js が X-Worker-Fallback を付けるので、それが無いことで確かめる）
  const csv = await fetch(`${base}/api/game/datasets.csv`);
  const csvText = await csv.text();
  check("/api/game/datasets.csv は 200 で CSV（アクセスキーなし）", csv.status === 200 && (csv.headers.get("content-type") ?? "").includes("text/csv") && csvText.startsWith("Key,"), `${csv.status} ${csv.headers.get("content-type")}`);
  check("/api/game/* は Worker を通らない（X-Worker-Fallback が無い）", csv.headers.get("x-worker-fallback") === null);
  const missingCsv = await fetch(`${base}/api/game/NoSuchGame/pokemon.csv`);
  const missingText = await missingCsv.text();
  check("無い /api/game/... は CSV として返らない（404 か SPA の index.html）",
    missingCsv.status === 404 || (missingCsv.status === 200 && (missingCsv.headers.get("content-type") ?? "").includes("text/html") && /<!doctype html|<html/i.test(missingText)),
    `${missingCsv.status} ${missingCsv.headers.get("content-type")}`);
  console.log(`     （参考）無い /api/game/... の応答: ${missingCsv.status} ${missingCsv.headers.get("content-type")}`);
  const oldData = await fetch(`${base}/data/datasets.csv`);
  check("以前の /data/datasets.csv は無い（404 か SPA の index.html）",
    oldData.status === 404 || (oldData.status === 200 && (oldData.headers.get("content-type") ?? "").includes("text/html")), `${oldData.status} ${oldData.headers.get("content-type")}`);

  // DELETE /api/my/access-key（キーの作り直し）
  const del = await req("DELETE", K, key);
  check("DELETE /api/my/access-key は 204", del.status === 204);
  check("DELETE 後の GET my-pokemon は 410", (await req("GET", P, key)).status === 410);
  check("DELETE 後の GET my-team は 410", (await req("GET", T, key)).status === 410);
  check("DELETE 後の GET my-rival は 410", (await req("GET", R, key)).status === 410);
  check("DELETE 後の PUT my-pokemon は 410", (await req("PUT", P, key, { baseRevision: 0, dataSets: { Champions: { pokemon: [] } } })).status === 410);
  check("DELETE 後の PUT my-team は 410", (await req("PUT", T, key, { baseRevision: 0, dataSets: { Champions: { teams: [], activeTeam: null } } })).status === 410);
  check("DELETE はもう一度呼んでも 204", (await req("DELETE", K, key)).status === 204);
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
