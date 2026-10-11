// 昔の世代のダメージ計算の期待値を、公開されているダメージ計算ツール（@smogon/calc）で付ける。
//
//   npm i --no-save @smogon/calc@0.12.0
//   node tools/calc_vectors/expected.js scenarios.json > tests/FaraPokemonAssistance.Core.Tests/Data/calc_vectors.json
//
// make_scenarios.py が作った条件を読み、サイトのデータとツールのデータ（種族値・タイプ・技の威力など）が
// 一致する条件だけを残して、乱数 16 通りのダメージを expected に入れる。データが違う条件や、
// ツールが特別に扱う（この計算機では未対応の）技は除き、除いた理由の件数を標準エラーに出す。
'use strict';
const fs = require('fs');
const path = require('path');
const { calculate, Generations, Pokemon, Move, Field } = require('@smogon/calc');

const root = path.resolve(__dirname, '..', '..');
const dataDir = path.join(root, 'src', 'FaraPokemonAssistance.Web', 'wwwroot', 'data');
const toID = (s) => ('' + s).toLowerCase().replace(/[^a-z0-9]+/g, '');

function readCsv(file) {
  const text = fs.readFileSync(file, 'utf8').replace(/\r/g, '');
  const lines = text.split('\n').filter((l) => l.length > 0);
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

// ツールが特別に扱う技（威力・分類・使う能力などが変わる）。この計算機では対応していないので比べない。
// からげんき・はたきおとす・ソーラービーム・かわらわり・サイコファング・じしん・じならし・サイコショックなどは対応しているので含めない。
const SPECIAL_MOVES = new Set([
  'payback', 'pursuit', 'electroball', 'gyroball', 'punishment', 'storedpower', 'powertrip', 'acrobatics',
  'assurance', 'wakeupslap', 'smellingsalts', 'weatherball', 'fling', 'hex', 'brine', 'venoshock', 'retaliate',
  'fusionbolt', 'fusionflare', 'eruption', 'waterspout', 'flail', 'reversal', 'crushgrip', 'wringout',
  'grassknot', 'lowkick', 'heatcrash', 'heavyslam', 'return', 'frustration', 'trumpcard', 'naturalgift',
  'naturepower', 'judgment', 'technoblast', 'multiattack', 'revelationdance', 'hiddenpower', 'spitup',
  'beatup', 'rollout', 'iceball', 'furycutter', 'echoedvoice', 'round', 'avalanche', 'revenge', 'magnitude',
  'present', 'triplekick', 'foulplay', 'bodypress', 'psywave', 'nightshade', 'seismictoss', 'dragonrage',
  'sonicboom', 'superfang', 'endeavor', 'finalgambit', 'counter', 'mirrorcoat', 'metalburst', 'bide',
  'naturesmadness', 'guardianofalola', 'spectralthief', 'skydrop', 'synchronoise', 'dreameater',
  'chipaway', 'sacredsword', 'darkestlariat', 'stormthrow', 'frostbreath', 'photongeyser',
  'lightthatburnsthesky', 'moongeistbeam', 'sunsteelstrike', 'menacingmoonrazemaelstrom', 'searingsunrazesmash',
  'thousandarrows', 'freezedry', 'flyingpress', 'pledge', 'grasspledge', 'firepledge', 'waterpledge', 'shellsidearm',
  'pikapapow', 'veeveevolley', 'struggle', 'futuresight', 'doomdesire', 'stompingtantrum', 'burnup',
  'fishiousrend', 'boltbeak', 'risingvoltage', 'expandingforce', 'terrainpulse', 'mistyexplosion',
  'steelroller', 'grassyglide', 'lashout', 'poltergeist', 'gravapple', 'snore', 'sleeptalk',
  'fakeout', 'lastresort', 'belch', 'stuffcheeks',
]);

const SMOGON_WEATHER = { Sun: 'Sun', Rain: 'Rain', Sand: 'Sand', Hail: 'Hail' };
const STATS = ['hp', 'atk', 'def', 'spa', 'spd', 'spe'];

function main() {
  const scenarios = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
  const skip = {};
  const bump = (k) => { skip[k] = (skip[k] || 0) + 1; };
  const cache = {};
  const out = [];
  for (const s of scenarios) {
    const gen = Generations.get(s.gen);
    if (!cache[s.key]) {
      cache[s.key] = {
        pokemon: Object.fromEntries(readCsv(path.join(dataDir, s.key, 'pokemon.csv')).map((r) => [r.Id, r])),
        moves: Object.fromEntries(readCsv(path.join(dataDir, s.key, 'moves.csv')).map((r) => [r.Id, r])),
      };
    }
    const ours = cache[s.key];
    const mon = (side) => {
      const species = gen.species.get(toID(side.smogon)) || gen.species.get(toID(side.smogon.replace(/-(normal|ordinary|altered|land|plant|average|aria|standard|incarnate|red-striped|shield|baile|midday|disguised|solo|meteor|50)$/, '')));
      if (!species) { bump('species-unknown'); return null; }
      const row = ours.pokemon[side.id];
      const bs = species.baseStats;
      const mine = [row.HP, row.Attack, row.Defense, row.SpAttack, row.SpDefense, row.Speed].map(Number);
      if (STATS.some((st, i) => bs[st] !== mine[i])) { bump('species-stats'); return null; }
      const types = [row.Type1, row.Type2].filter((t) => t);
      if (species.types.length !== types.length || species.types.some((t, i) => t !== types[i])) { bump('species-types'); return null; }
      const ability = gen.abilities.get(toID(side.abilitySmogon));
      if (!ability) { bump('ability-unknown'); return null; }
      let item;
      if (side.itemSmogon) {
        const it = gen.items.get(toID(side.itemSmogon));
        if (!it) { bump('item-unknown'); return null; }
        item = it.name;
        if (toID(item) === 'eviolite' && !!species.nfe !== (row.NotFullyEvolved === '1')) { bump('nfe-mismatch'); return null; }
      }
      const evs = {}; STATS.forEach((st, i) => { evs[st] = side.evs[i]; });
      const boosts = { atk: side.boosts[0], def: side.boosts[1], spa: side.boosts[2], spd: side.boosts[3], spe: side.boosts[4] };
      const p = new Pokemon(gen, species.name, {
        level: side.level, ability: ability.name, item, nature: side.natureSmogon, evs, boosts,
        status: side.status || '', curHP: side.hp || undefined,
      });
      return p;
    };
    const attacker = mon(s.attacker);
    if (!attacker) continue;
    const defender = mon(s.defender);
    if (!defender) continue;
    const mid = toID(s.move.smogon);
    const md = gen.moves.get(mid);
    if (!md) { bump('move-unknown'); continue; }
    if (SPECIAL_MOVES.has(mid)) { bump('move-special'); continue; }
    if (md.multihit || md.ignoreDefensive || md.overrideOffensiveStat || md.overrideOffensivePokemon || md.willCrit) { bump('move-special'); continue; }
    const om = ours.moves[s.move.id];
    const cat = new Move(gen, md.name).category;
    if (md.basePower !== Number(om.Power) || md.type !== om.Type || cat !== om.Category) { bump('move-data'); continue; }
    // 複数対象かどうか（第 3 世代は相手 2 体だけの技かどうか）もデータと合っているものだけ
    const target = md.target === 'allAdjacentFoes' ? 'AllFoes' : md.target === 'allAdjacent' ? 'AllOthers' : 'Single';
    const ourTarget = om.Target === 'AllFoes' || om.Target === 'AllOthers' ? om.Target : 'Single';
    if (s.doubles && (s.gen === 3 ? (target === 'AllFoes') !== (ourTarget === 'AllFoes') : (target === 'Single') !== (ourTarget === 'Single'))) { bump('move-target'); continue; }
    const useZ = !!s.z && !!(md.zMove && md.zMove.basePower);
    const move = new Move(gen, md.name, { isCrit: s.crit, useZ, item: s.attacker.itemSmogon ? attacker.item : undefined });
    const field = new Field({
      gameType: s.doubles ? 'Doubles' : 'Singles',
      weather: SMOGON_WEATHER[s.weather],
      terrain: s.terrain || undefined,
      defenderSide: { isReflect: s.reflect, isLightScreen: s.lightScreen },
    });
    let result;
    try {
      result = calculate(gen, attacker, defender, move, field);
    } catch (e) { bump('calc-error'); continue; }
    let damage = result.damage;
    if (typeof damage === 'number') damage = Array(16).fill(damage);
    if (!Array.isArray(damage) || Array.isArray(damage[0])) { bump('damage-shape'); continue; }
    if (attacker.maxHP() <= 0) { bump('hp'); continue; }
    out.push({ ...s, z: useZ, expected: damage, expectedMaxHp: defender.maxHP() });
  }
  process.stderr.write(`kept ${out.length} / ${scenarios.length}; skipped ${JSON.stringify(skip)}\n`);
  process.stdout.write(JSON.stringify(out));
}

main();
