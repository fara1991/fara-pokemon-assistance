using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>
/// 第5世代以降のダメージ計算。乱数 16 通りをすべて返す。
/// 対応: 実数値・ランク補正・急所・タイプ一致・タイプ相性・ダブルの複数対象・主要な持ち物。
/// 未対応: 特性・天候・フィールド・テラスタル・やけど・壁（今後追加予定）。
/// </summary>
public sealed class DamageCalculator
{
    private readonly TypeChart _typeChart;

    public DamageCalculator(TypeChart typeChart)
    {
        _typeChart = typeChart;
    }

    public DamageResult Calculate(DamageRequest request)
    {
        var attacker = request.Attacker;
        var defender = request.Defender;
        var move = request.Move;
        var modifiers = new List<string>();

        var defenderHp = StatCalculator.Calculate(defender, Stat.HP);
        var effectiveness = _typeChart.Against(move.Type, defender.Pokemon);

        if (!move.IsDamaging)
        {
            return new DamageResult
            {
                Rolls = new int[16],
                DefenderHP = defenderHp,
                TypeEffectiveness = effectiveness,
                IsStab = false,
                AttackStat = 0,
                DefenseStat = 0,
                BasePower = 0,
                KnockOut = new KnockOut(0, 0),
                Modifiers = new[] { "変化技" },
            };
        }

        var isPhysical = move.Category == MoveCategory.Physical;
        var attackStatKind = isPhysical ? Stat.Attack : Stat.SpAttack;
        var defenseStatKind = isPhysical ? Stat.Defense : Stat.SpDefense;

        // --- 威力補正
        var power = move.Power;
        var powerMod = 4096;
        if (attacker.Item is { } atkItem && !string.IsNullOrEmpty(atkItem.TypeBoost))
        {
            var matches = atkItem.TypeBoost == move.Type || atkItem.TypeBoost == move.Category.ToString();
            if (matches)
            {
                powerMod = PokeRound(powerMod * atkItem.TypeBoostMultiplier);
                modifiers.Add($"{atkItem.Name}(威力{atkItem.TypeBoostMultiplier:0.##}倍)");
            }
        }
        power = Math.Max(1, PokeRound(power * powerMod / 4096.0));

        // --- 攻撃・防御の実数値（ランク → 持ち物）
        var attackStat = StatCalculator.Calculate(attacker, attackStatKind);
        var attackBoost = attacker.Boosts[attackStatKind];
        if (request.IsCritical && attackBoost < 0) attackBoost = 0;
        attackStat = StatCalculator.ApplyBoost(attackStat, attackBoost);
        if (attackBoost != 0) modifiers.Add($"攻撃側ランク{(attackBoost > 0 ? "+" : "")}{attackBoost}");

        var defenseStat = StatCalculator.Calculate(defender, defenseStatKind);
        var defenseBoost = defender.Boosts[defenseStatKind];
        if (request.IsCritical && defenseBoost > 0) defenseBoost = 0;
        defenseStat = StatCalculator.ApplyBoost(defenseStat, defenseBoost);
        if (defenseBoost != 0) modifiers.Add($"防御側ランク{(defenseBoost > 0 ? "+" : "")}{defenseBoost}");

        var attackItemMod = AttackItemModifier(attacker, isPhysical, modifiers);
        if (attackItemMod != 1.0) attackStat = Math.Max(1, PokeRound(attackStat * attackItemMod));

        var defenseItemMod = DefenseItemModifier(defender, isPhysical, modifiers);
        if (defenseItemMod != 1.0) defenseStat = Math.Max(1, PokeRound(defenseStat * defenseItemMod));

        // --- 基礎ダメージ
        var levelTerm = attacker.Level * 2 / 5 + 2;
        var baseDamage = (levelTerm * power * attackStat / defenseStat) / 50 + 2;

        // --- 複数対象（ダブル）
        if (request.Format == BattleFormat.Doubles && move.Target != MoveTarget.Single)
        {
            baseDamage = PokeRound(baseDamage * 0.75);
            modifiers.Add("複数対象(0.75倍)");
        }

        // --- 急所
        if (request.IsCritical)
        {
            baseDamage = PokeRound(baseDamage * 1.5);
            modifiers.Add("急所(1.5倍)");
        }

        // --- タイプ一致
        var isStab = attacker.Pokemon.HasType(move.Type);
        if (isStab) modifiers.Add("タイプ一致(1.5倍)");

        // --- 最終補正（持ち物）
        var finalMod = 4096;
        if (attacker.Item is { } item)
        {
            if (item.Category == ItemCategory.LifeOrb)
            {
                finalMod = PokeRound(finalMod * 5324 / 4096.0);
                modifiers.Add($"{item.Name}(1.3倍)");
            }
            else if (item.Category == ItemCategory.ExpertBelt && effectiveness > 1)
            {
                finalMod = PokeRound(finalMod * 4915 / 4096.0);
                modifiers.Add($"{item.Name}(1.2倍)");
            }
            else if (item.Category is not (ItemCategory.LifeOrb or ItemCategory.ExpertBelt) && item.DamageMultiplier != 1.0)
            {
                finalMod = PokeRound(finalMod * item.DamageMultiplier);
                modifiers.Add($"{item.Name}({item.DamageMultiplier:0.##}倍)");
            }
        }

        if (effectiveness != 1.0) modifiers.Add(effectiveness == 0 ? "効果なし" : $"タイプ相性{effectiveness:0.##}倍");

        var rolls = new int[16];
        for (var i = 0; i < 16; i++)
        {
            var random = 85 + i;
            var damage = baseDamage * random / 100;
            if (isStab) damage = PokeRound(damage * 1.5);
            damage = (int)Math.Floor(damage * effectiveness);
            damage = PokeRound(damage * finalMod / 4096.0);
            if (effectiveness > 0 && damage < 1) damage = 1;
            rolls[i] = damage;
        }

        return new DamageResult
        {
            Rolls = rolls,
            DefenderHP = defenderHp,
            TypeEffectiveness = effectiveness,
            IsStab = isStab,
            AttackStat = attackStat,
            DefenseStat = defenseStat,
            BasePower = power,
            KnockOut = KnockOutCalculator.Calculate(rolls, defenderHp),
            Modifiers = modifiers,
        };
    }

    private static double AttackItemModifier(PokemonBuild attacker, bool isPhysical, List<string> modifiers)
    {
        var item = attacker.Item;
        if (item is null) return 1.0;

        switch (item.Category)
        {
            case ItemCategory.LightBall when attacker.Pokemon.SpeciesId == 25:
                modifiers.Add($"{item.Name}(2倍)");
                return 2.0;
            case ItemCategory.ThickClub when isPhysical && attacker.Pokemon.SpeciesId is 104 or 105:
                modifiers.Add($"{item.Name}(2倍)");
                return 2.0;
        }

        var mod = isPhysical ? item.AttackMultiplier : item.SpAttackMultiplier;
        if (mod != 1.0) modifiers.Add($"{item.Name}({(isPhysical ? "攻撃" : "特攻")}{mod:0.##}倍)");
        return mod;
    }

    private static double DefenseItemModifier(PokemonBuild defender, bool isPhysical, List<string> modifiers)
    {
        var item = defender.Item;
        if (item is null) return 1.0;

        if (item.Category == ItemCategory.Eviolite)
        {
            if (!defender.Pokemon.NotFullyEvolved) return 1.0;
            modifiers.Add($"{item.Name}(1.5倍)");
            return 1.5;
        }

        var mod = isPhysical ? item.DefenseMultiplier : item.SpDefenseMultiplier;
        if (mod != 1.0) modifiers.Add($"{item.Name}({(isPhysical ? "防御" : "特防")}{mod:0.##}倍)");
        return mod;
    }

    /// <summary>五捨五超入（0.5 ちょうどは切り捨て）。</summary>
    public static int PokeRound(double value)
    {
        var floor = Math.Floor(value);
        return value - floor > 0.5 ? (int)floor + 1 : (int)floor;
    }
}

/// <summary>乱数 16 通りから確定数と確率を求める。回復・定数ダメージは考慮しない。</summary>
public static class KnockOutCalculator
{
    public const int MaxHits = 10;

    public static KnockOut Calculate(int[] rolls, int hp)
    {
        if (rolls.Length == 0 || rolls[^1] <= 0 || hp <= 0)
            return new KnockOut(0, 0);

        // 合計ダメージの分布を畳み込みで求める（HP 以上はまとめる）
        var dist = new Dictionary<int, double> { [0] = 1.0 };
        var perRoll = 1.0 / rolls.Length;
        for (var hits = 1; hits <= MaxHits; hits++)
        {
            var next = new Dictionary<int, double>();
            foreach (var (sum, p) in dist)
            {
                foreach (var roll in rolls)
                {
                    var s = Math.Min(hp, sum + roll);
                    next[s] = next.GetValueOrDefault(s) + p * perRoll;
                }
            }
            dist = next;
            var ko = dist.GetValueOrDefault(hp);
            if (ko > 0)
                return new KnockOut(hits, Math.Min(1.0, ko));
        }
        return new KnockOut(MaxHits + 1, 0);
    }
}
