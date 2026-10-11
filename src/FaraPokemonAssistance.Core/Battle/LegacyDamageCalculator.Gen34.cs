using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>
/// 第 3〜4 世代のダメージ計算（@smogon/calc の gen3 / gen4 と同じ順番・切り捨て）。
/// 4096 を 1 倍とする補正ではなく、補正ごとに切り捨てる。第 3 世代は物理・特殊が技のタイプで決まり（データ側で反映済み）、
/// タイプ一致がタイプ相性の前・乱数の前に掛かる。
/// </summary>
internal sealed partial class LegacyDamageCalculator
{
    private const int MoveIdFireFang = 424;
    private const int MoveIdExplosion = 153;
    private const int MoveIdSelfDestruct = 120;

    /// <summary>2 タイプのポケモンで、どちらのタイプの相性を先に掛けるか（この順で前のタイプが先）。</summary>
    private static readonly string[] TypePrecedence =
    {
        "Normal", "Fire", "Water", "Electric", "Grass", "Ice", "Fighting", "Poison", "Ground", "Flying",
        "Psychic", "Bug", "Rock", "Ghost", "Dragon", "Dark", "Steel",
    };

    /// <summary>第 4 世代のかたやぶりで無視される防御側の特性（この計算で効果を扱うもの）。</summary>
    private static readonly HashSet<string> IgnorableAbilitiesGen4 = new()
    {
        "battle-armor", "shell-armor", "dry-skin", "filter", "flash-fire", "heatproof", "levitate", "lightning-rod",
        "marvel-scale", "motor-drive", "solid-rock", "soundproof", "storm-drain", "thick-fat", "unaware",
        "volt-absorb", "water-absorb", "wonder-guard",
    };

    private DamageResult CalculateGen34(DamageRequest request)
    {
        var attacker = request.Attacker;
        var defender = request.Defender;
        var move = request.Move;
        var modifiers = new List<string>();
        var defenderHp = defender.MaxHp;
        if (!move.IsDamaging)
            return Empty(defenderHp, 1.0, move.Type, "変化技");

        var atkAbility = attacker.Ability?.Identifier ?? "";
        var defAbility = defender.Ability?.Identifier ?? "";
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";
        var attackerItem = attacker.Item;
        var defenderItem = defender.Item;

        var weather = request.Weather;
        if (weather != Weather.None && (atkAbility is "air-lock" or "cloud-nine" || defAbility is "air-lock" or "cloud-nine"))
        {
            modifiers.Add($"{(atkAbility is "air-lock" or "cloud-nine" ? atkName : defName)}(天候の効果なし)");
            weather = Weather.None;
        }

        if (_gen == 4 && atkAbility == "mold-breaker" && IgnorableAbilitiesGen4.Contains(defAbility))
        {
            modifiers.Add($"{atkName}(相手の特性を無視)");
            defAbility = "";
        }

        var moveType = move.Type;
        if (_gen == 4 && atkAbility == "normalize")
        {
            moveType = "Normal";
            modifiers.Add($"{atkName}(ノーマルタイプ化)");
        }

        var reflect = request.Reflect || request.Screen;
        var lightScreen = request.LightScreen || request.Screen;
        if (move.Id == MoveIdBrickBreak) reflect = lightScreen = false;

        // --- 相性（2 タイプは決まった順に、1 つずつ切り捨てながら掛ける）
        var types = defender.Pokemon.Types.ToList();
        if (types.Count == 2 && types[0] != types[1] && Array.IndexOf(TypePrecedence, types[0]) > Array.IndexOf(TypePrecedence, types[1]))
            (types[0], types[1]) = (types[1], types[0]);
        double Against(string t)
        {
            var m = _chart.Against(moveType, t);
            if (m == 0 && t == "Ghost" && _gen == 4 && atkAbility == "scrappy" && moveType is "Normal" or "Fighting") m = 1;
            return m;
        }
        var eff1 = Against(types.Count > 0 ? types[0] : "");
        var eff2 = types.Count > 1 ? Against(types[1]) : 1.0;
        var effectiveness = eff1 * eff2;
        if (effectiveness == 0)
            return Empty(defenderHp, 0, moveType, "効果なし");

        var flags = move.Flags;
        string? immunity = _gen == 3
            ? defAbility switch
            {
                "flash-fire" when moveType == "Fire" => "ほのお技無効",
                "levitate" when moveType == "Ground" => "じめん技無効",
                "volt-absorb" when moveType == "Electric" => "でんき技無効",
                "water-absorb" when moveType == "Water" => "みず技無効",
                "wonder-guard" when effectiveness <= 1 => "効果抜群以外無効",
                "soundproof" when flags.Contains("Sound") => "音技無効",
                _ => null,
            }
            : defAbility switch
            {
                "wonder-guard" when effectiveness <= 1 && move.Id != MoveIdFireFang => "効果抜群以外無効",
                "flash-fire" when moveType == "Fire" => "ほのお技無効",
                "dry-skin" or "water-absorb" when moveType == "Water" => "みず技無効",
                "motor-drive" or "volt-absorb" when moveType == "Electric" => "でんき技無効",
                "levitate" when moveType == "Ground" => "じめん技無効",
                "soundproof" when flags.Contains("Sound") => "音技無効",
                _ => null,
            };
        if (immunity is not null)
            return Empty(defenderHp, 0, moveType, $"{defName}({immunity})");

        var isCritical = request.IsCritical && defAbility is not ("battle-armor" or "shell-armor");
        if (request.IsCritical && !isCritical) modifiers.Add($"{defName}(急所に当たらない)");
        var isPhysical = move.Category == MoveCategory.Physical;
        var statName = isPhysical ? "攻撃" : "特攻";
        var pinch = attacker.CurrentHp * 3 <= attacker.MaxHp && (atkAbility == "overgrow" && moveType == "Grass" || atkAbility == "blaze" && moveType == "Fire"
            || atkAbility == "torrent" && moveType == "Water" || atkAbility == "swarm" && moveType == "Bug");

        // --- 威力
        var basePower = move.Power;
        if (move.Id == MoveIdFacade && attacker.Status is StatusCondition.Burn or StatusCondition.Paralysis or StatusCondition.Poison or StatusCondition.BadlyPoisoned)
        {
            basePower *= 2;
            modifiers.Add("からげんき(状態異常で2倍)");
        }
        if (_gen == 4)
        {
            if (atkAbility == "technician" && basePower <= 60) { basePower = basePower * 3 / 2; modifiers.Add($"{atkName}(1.5倍)"); }
            if (IsMuscleBand(attackerItem, isPhysical)) { basePower = (int)Math.Floor(basePower * 1.1); modifiers.Add($"{attackerItem!.Name}(1.1倍)"); }
            else if (IsTypeBoost(attackerItem, moveType)) { basePower = (int)Math.Floor(basePower * 1.2); modifiers.Add($"{attackerItem!.Name}(1.2倍)"); }
            if (atkAbility == "iron-fist" && flags.Contains("Punch") || atkAbility == "reckless" && MoveEffects.IsRecoil(move)) { basePower = (int)Math.Floor(basePower * 1.2); modifiers.Add($"{atkName}(1.2倍)"); }
            else if (pinch) { basePower = basePower * 3 / 2; modifiers.Add($"{atkName}(HP1/3以下 1.5倍)"); }
            if (defAbility == "heatproof" && moveType == "Fire" || defAbility == "thick-fat" && moveType is "Fire" or "Ice")
            {
                basePower /= 2;
                modifiers.Add($"{defName}(0.5倍)");
            }
            else if (defAbility == "dry-skin" && moveType == "Fire") { basePower = (int)Math.Floor(basePower * 1.25); modifiers.Add($"{defName}(1.25倍)"); }
        }
        else if (pinch)
        {
            basePower = basePower * 3 / 2;
            modifiers.Add($"{atkName}(HP1/3以下 1.5倍)");
        }

        // --- 攻撃側の実数値
        var attackStatKind = isPhysical ? Stat.Attack : Stat.SpAttack;
        var attack = StatCalculator.Calculate(attacker, attackStatKind);
        var attackBoost = attacker.Boosts[attackStatKind];
        var applyAttackBoost = attackBoost > 0 || !isCritical && attackBoost < 0;
        if (_gen == 4)
        {
            if (defAbility == "unaware") { if (attackBoost != 0) modifiers.Add($"{defName}(ランク無視)"); }
            else if (applyAttackBoost) { attack = StatCalculator.ApplyBoost(attack, attackBoost); modifiers.Add($"攻撃側ランク{(attackBoost > 0 ? "+" : "")}{attackBoost}"); }
            if (isPhysical && atkAbility is "huge-power" or "pure-power") { attack *= 2; modifiers.Add($"{atkName}({statName}2倍)"); }
            else if (!isPhysical && atkAbility == "solar-power" && weather == Weather.Sun) { attack = attack * 3 / 2; modifiers.Add($"{atkName}({statName}1.5倍)"); }
            else if (isPhysical && (atkAbility == "hustle" || atkAbility == "guts" && attacker.Status != StatusCondition.None)) { attack = attack * 3 / 2; modifiers.Add($"{atkName}({statName}1.5倍)"); }
            if (IsChoiceBand(attackerItem) && isPhysical || IsChoiceSpecs(attackerItem) && !isPhysical) { attack = attack * 3 / 2; modifiers.Add($"{attackerItem!.Name}({statName}1.5倍)"); }
            else if (attackerItem is { Category: ItemCategory.LightBall } && attacker.Pokemon.SpeciesId == 25
                || attackerItem is { Category: ItemCategory.ThickClub } && isPhysical && attacker.Pokemon.SpeciesId is 104 or 105)
            {
                attack *= 2;
                modifiers.Add($"{attackerItem.Name}({statName}2倍)");
            }
        }
        else
        {
            if (isPhysical && atkAbility is "huge-power" or "pure-power") { attack *= 2; modifiers.Add($"{atkName}({statName}2倍)"); }
            // 第 3 世代のタイプ強化の持ち物は攻撃・特攻の 1.1 倍
            if (IsTypeBoost(attackerItem, moveType)) { attack = (int)Math.Floor(attack * 1.1); modifiers.Add($"{attackerItem!.Name}({statName}1.1倍)"); }
            else if (IsChoiceBand(attackerItem) && isPhysical) { attack = attack * 3 / 2; modifiers.Add($"{attackerItem!.Name}({statName}1.5倍)"); }
            else if (attackerItem is { Category: ItemCategory.LightBall } && !isPhysical && attacker.Pokemon.SpeciesId == 25
                || attackerItem is { Category: ItemCategory.ThickClub } && isPhysical && attacker.Pokemon.SpeciesId is 104 or 105)
            {
                attack *= 2;
                modifiers.Add($"{attackerItem.Name}({statName}2倍)");
            }
            if (defAbility == "thick-fat" && moveType is "Fire" or "Ice") { attack /= 2; modifiers.Add($"{defName}({statName}0.5倍)"); }
            if (isPhysical && (atkAbility == "hustle" || atkAbility == "guts" && attacker.Status != StatusCondition.None)) { attack = attack * 3 / 2; modifiers.Add($"{atkName}({statName}1.5倍)"); }
            if (applyAttackBoost) { attack = StatCalculator.ApplyBoost(attack, attackBoost); modifiers.Add($"攻撃側ランク{(attackBoost > 0 ? "+" : "")}{attackBoost}"); }
        }

        // --- 防御側の実数値
        var defenseStatKind = isPhysical ? Stat.Defense : Stat.SpDefense;
        var defStatName = isPhysical ? "防御" : "特防";
        var defense = StatCalculator.Calculate(defender, defenseStatKind);
        var defenseBoost = defender.Boosts[defenseStatKind];
        var applyDefenseBoost = defenseBoost < 0 || !isCritical && defenseBoost > 0;
        var selfDestruct = move.Id is MoveIdExplosion or MoveIdSelfDestruct;
        if (_gen == 4)
        {
            if (atkAbility == "unaware") { if (defenseBoost != 0) modifiers.Add($"{atkName}(ランク無視)"); }
            else if (applyDefenseBoost) { defense = StatCalculator.ApplyBoost(defense, defenseBoost); modifiers.Add($"防御側ランク{(defenseBoost > 0 ? "+" : "")}{defenseBoost}"); }
            if (defAbility == "marvel-scale" && defender.Status != StatusCondition.None && isPhysical) { defense = defense * 3 / 2; modifiers.Add($"{defName}({defStatName}1.5倍)"); }
            if (weather == Weather.Sand && defender.Pokemon.HasType("Rock") && !isPhysical) { defense = defense * 3 / 2; modifiers.Add("砂嵐(いわタイプの特防1.5倍)"); }
            if (selfDestruct) { defense /= 2; modifiers.Add($"{move.Name}(相手の防御半分)"); }
            defense = Math.Max(1, defense);
        }
        else
        {
            if (defAbility == "marvel-scale" && defender.Status != StatusCondition.None && isPhysical) { defense = defense * 3 / 2; modifiers.Add($"{defName}({defStatName}1.5倍)"); }
            if (selfDestruct) { defense /= 2; modifiers.Add($"{move.Name}(相手の防御半分)"); }
            if (applyDefenseBoost) { defense = StatCalculator.ApplyBoost(defense, defenseBoost); modifiers.Add($"防御側ランク{(defenseBoost > 0 ? "+" : "")}{defenseBoost}"); }
        }

        // --- 基礎ダメージと補正
        var levelTerm = attacker.Level * 2 / 5 + 2;
        var burn = attacker.Status == StatusCondition.Burn && isPhysical && atkAbility != "guts";
        var doubles = request.Format == BattleFormat.Doubles;
        int baseDamage;
        if (_gen == 4)
        {
            baseDamage = levelTerm * basePower * attack / 50 / defense;
            if (burn) { baseDamage /= 2; modifiers.Add("やけど(物理0.5倍)"); }
        }
        else
        {
            baseDamage = levelTerm * attack * basePower / defense / 50;
            if (burn) { baseDamage /= 2; modifiers.Add("やけど(物理0.5倍)"); }
        }
        if (!isCritical && (isPhysical && reflect || !isPhysical && lightScreen))
        {
            var wall = isPhysical ? "リフレクター" : "ひかりのかべ";
            baseDamage = (int)Math.Floor(baseDamage * (doubles ? 2.0 / 3 : 0.5));
            modifiers.Add(doubles ? $"{wall}(ダブル 0.67倍)" : $"{wall}(0.5倍)");
        }
        if (doubles && (_gen == 4 ? move.Target != MoveTarget.Single : move.Target == MoveTarget.AllFoes))
        {
            baseDamage = _gen == 4 ? baseDamage * 3 / 4 : baseDamage / 2;
            modifiers.Add(_gen == 4 ? "複数対象(0.75倍)" : "複数対象(0.5倍)");
        }
        if (weather == Weather.Sun && moveType == "Fire" || weather == Weather.Rain && moveType == "Water")
        {
            baseDamage = baseDamage * 3 / 2;
            modifiers.Add($"{FieldNames.Japanese(weather, _gen)}(1.5倍)");
        }
        else if (weather == Weather.Sun && moveType == "Water" || weather == Weather.Rain && moveType == "Fire"
            || move.Id == MoveIdSolarBeam && weather is Weather.Rain or Weather.Sand or Weather.Snow)
        {
            baseDamage /= 2;
            modifiers.Add($"{FieldNames.Japanese(weather, _gen)}(0.5倍)");
        }
        baseDamage = (_gen == 3 && isPhysical ? Math.Max(1, baseDamage) : baseDamage) + 2;
        if (isCritical)
        {
            var crit = _gen == 4 && atkAbility == "sniper" ? 3 : 2;
            baseDamage *= crit;
            modifiers.Add($"急所({crit}倍)");
        }

        var isStab = attacker.Pokemon.HasType(moveType);
        if (_gen == 4)
        {
            if (attackerItem is { Category: ItemCategory.LifeOrb }) { baseDamage = (int)Math.Floor(baseDamage * 1.3); modifiers.Add($"{attackerItem.Name}(1.3倍)"); }
            var stab = isStab ? (atkAbility == "adaptability" ? 2.0 : 1.5) : 1.0;
            if (isStab) modifiers.Add($"タイプ一致({stab:0.#}倍)");
            if (effectiveness != 1.0) modifiers.Add($"タイプ相性{effectiveness:0.##}倍");
            var filter = defAbility is "filter" or "solid-rock" && effectiveness > 1;
            var belt = attackerItem is { Category: ItemCategory.ExpertBelt } && effectiveness > 1;
            var tinted = atkAbility == "tinted-lens" && effectiveness < 1;
            var berryApplies = defenderItem is { Category: ItemCategory.ResistBerry } berry && berry.TypeBoost == moveType && (effectiveness > 1 || moveType == "Normal");
            if (filter) modifiers.Add($"{defName}(0.75倍)");
            if (belt) modifiers.Add($"{attackerItem!.Name}(1.2倍)");
            if (tinted) modifiers.Add($"{atkName}(2倍)");
            if (berryApplies) modifiers.Add($"{defenderItem!.Name}(1発目のみ 0.5倍)");

            int[] Rolls(bool withBerry)
            {
                var rolls = new int[16];
                for (var i = 0; i < 16; i++)
                {
                    var d = baseDamage * (85 + i) / 100;
                    d = (int)Math.Floor(d * stab);
                    d = (int)Math.Floor(d * eff1);
                    d = (int)Math.Floor(d * eff2);
                    if (filter) d = (int)Math.Floor(d * 0.75);
                    if (belt) d = (int)Math.Floor(d * 1.2);
                    if (tinted) d *= 2;
                    if (withBerry) d /= 2;
                    rolls[i] = Math.Max(1, d);
                }
                return rolls;
            }
            var first = Rolls(berryApplies);
            var later = berryApplies ? Rolls(false) : first;
            return Result(first, later, effectiveness, isStab, attack, defense, basePower, moveType);
        }
        else
        {
            if (isStab) { baseDamage = baseDamage * 3 / 2; modifiers.Add("タイプ一致(1.5倍)"); }
            baseDamage = (int)Math.Floor(baseDamage * eff1);
            baseDamage = (int)Math.Floor(baseDamage * eff2);
            if (effectiveness != 1.0) modifiers.Add($"タイプ相性{effectiveness:0.##}倍");
            var rolls = new int[16];
            for (var i = 0; i < 16; i++) rolls[i] = Math.Max(1, baseDamage * (85 + i) / 100);
            return Result(rolls, rolls, effectiveness, isStab, attack, defense, basePower, moveType);
        }

        DamageResult Result(int[] rolls, int[] laterRolls, double eff, bool stab, int atk, int def, int bp, string type)
        {
            var koOptions = KnockOutOptions.For(request, defender, defenderHp, !defender.Pokemon.HasType("Flying") && defAbility != "levitate",
                laterRolls, modifiers, _gen);
            return new DamageResult
            {
                Rolls = rolls,
                DefenderHP = defenderHp,
                TypeEffectiveness = eff,
                IsStab = stab,
                AttackStat = atk,
                DefenseStat = def,
                BasePower = bp,
                MoveType = type,
                KnockOut = KnockOutCalculator.Calculate(rolls, defenderHp, koOptions),
                Modifiers = modifiers,
            };
        }
    }
}
