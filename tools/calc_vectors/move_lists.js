// 技ごとの「ちからずくで威力が上がる」「すてみ（反動技）で威力が上がる」の期待値を、公開されているダメージ計算ツール（@smogon/calc）から作る。
//
//   npm i --no-save @smogon/calc@0.12.0
//   node tools/calc_vectors/move_lists.js <PokeAPI CSV のキャッシュ> > tests/FaraPokemonAssistance.Core.Tests/Data/move_lists.json
//
// データセットごとに、サイトの技（PokeAPI の識別子で対応付け）のうちツールにも同じ技があるものを known に入れ、
// そのうち追加効果がある技を sheerForce、反動技（はずすと自分がダメージを受ける技を含む）を recoil に入れる。
'use strict';
const fs = require('fs');
const path = require('path');
const { Generations } = require('@smogon/calc');

const root = path.resolve(__dirname, '..', '..');
const dataDir = path.join(root, 'src', 'FaraPokemonAssistance.Web', 'wwwroot', 'data');
const toID = (s) => ('' + s).toLowerCase().replace(/[^a-z0-9]+/g, '');

function readCsv(file) {
  const lines = fs.readFileSync(file, 'utf8').replace(/\r/g, '').split('\n').filter((l) => l.length > 0);
  const parse = (line) => {
    const out = []; let cur = ''; let q = false;
    for (let i = 0; i < line.length; i++) {
      const c = line[i];
      if (q) { if (c === '"' && line[i + 1] === '"') { cur += '"'; i++; } else if (c === '"') q = false; else cur += c; }
      else if (c === '"') q = true; else if (c === ',') { out.push(cur); cur = ''; } else cur += c;
    }
    out.push(cur); return out;
  };
  const header = parse(lines[0]);
  return lines.slice(1).map((l) => { const v = parse(l); const o = {}; header.forEach((h, i) => { o[h] = v[i]; }); return o; });
}

const cache = process.argv[2];
const identifiers = Object.fromEntries(readCsv(path.join(cache, 'moves.csv')).map((r) => [r.id, r.identifier]));
const out = {};
for (const key of ['Gen3', 'Gen4', 'Gen5', 'Gen6', 'Gen7', 'Gen8', 'Gen9', 'Champions']) {
  const gen = Generations.get(key === 'Champions' ? 9 : Number(key.slice(3)));
  const entry = { known: [], sheerForce: [], recoil: [] };
  for (const row of readCsv(path.join(dataDir, key, 'moves.csv'))) {
    const md = gen.moves.get(toID(identifiers[row.Id] || ''));
    if (!md) continue;
    const id = Number(row.Id);
    entry.known.push(id);
    // ちからずくは第 5 世代から（ツールは第 3・4 世代でも secondaries を持つので、第 5 世代以降だけ見る）
    if (gen.num >= 5 && (md.secondaries || ['electroshot', 'orderup'].includes(toID(md.name)))) entry.sheerForce.push(id);
    if (gen.num >= 4 && (md.recoil || md.hasCrashDamage)) entry.recoil.push(id);
  }
  out[key] = entry;
}
process.stdout.write(JSON.stringify(out));
