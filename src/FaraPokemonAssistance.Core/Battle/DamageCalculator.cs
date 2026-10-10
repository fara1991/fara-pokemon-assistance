using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>
/// 第9世代のダメージ計算。乱数 16 通りをすべて返す。
/// 対応: 実数値・ランク補正・急所・タイプ一致・タイプ相性・ダブルの複数対象・持ち物・
/// テラスタル・天候・フィールド・主要な特性（<see cref="AbilityEffects"/> 参照）。
/// 壁・やけど・HP 依存の特性（もうか等・マルチスケイル）・回復きのみ / たべのこし / 半減きのみも反映する。
/// 未対応: 一部の技固有処理（威力が変動する技など）。
/// </summary>
public sealed class DamageCalculator
{
    private const int MoveIdEarthquake = 89;
    private const int MoveIdMagnitude = 222;
    private const int MoveIdBulldoze = 523;
    private const int MoveIdExpandingForce = 797;
    private const int MoveIdRisingVoltage = 896;
    private const int MoveIdPsyblade = 919;
    private const int ItemIdAirBalloon = 584;
    private const int ItemIdBoosterEnergy = 1696;
    private const int ItemIdPunchingGlove = 1700;
    private const int MoveIdFacade = 263;

    private readonly TypeChart _typeChart;

    public DamageCalculator(TypeChart typeChart)
    {
        _typeChart = typeChart;
    }

    /// <summary>ダイマックス技の威力（かくとう・どくは低めの表）。</summary>
    public static int MaxMovePower(Move move)
    {
        var bp = move.Power;
        if (bp <= 0) return bp;
        var low = move.Type is "Fighting" or "Poison";
        return bp switch
        {
            <= 40 => low ? 70 : 90,
            <= 50 => low ? 75 : 100,
            <= 60 => low ? 80 : 110,
            <= 70 => low ? 85 : 120,
            <= 100 => low ? 90 : 130,
            <= 140 => low ? 95 : 140,
            _ => low ? 100 : 150,
        };
    }

    public DamageResult Calculate(DamageRequest request)
    {
        var attacker = request.Attacker;
        var defender = request.Defender;
        var move = request.Move;
        var modifiers = new List<string>();
        var defenderHp = defender.MaxHp;
        if (defender.IsDynamax) modifiers.Add("防御側ダイマックス(HP2倍)");

        if (!move.IsDamaging)
            return Empty(defenderHp, 1.0, move.Type, "変化技");

        // かたやぶり系は防御側の特性を無視する
        var attackerAbility = attacker.Ability?.Identifier ?? "";
        var defenderAbility = defender.Ability?.Identifier ?? "";
        if (AbilityEffects.IgnoresTargetAbility(attackerAbility) && defenderAbility.Length > 0)
        {
            modifiers.Add($"{attacker.Ability!.Name}(相手の特性を無視)");
            defenderAbility = "";
        }

        var isPhysical = move.Category == MoveCategory.Physical;
        var attackStatKind = move.AttackStatUsed;
        var defenseStatKind = move.DefenseStatUsed;
        if (move.UsesPhysicalDefense) modifiers.Add($"{move.Name}(相手の防御で計算)");

        // --- 技タイプ（スキン系）
        var moveType = move.Type;
        var powerMod = 4096;
        var skinType = AbilityEffects.SkinType(attackerAbility);
        if (skinType is not null && moveType == "Normal")
        {
            moveType = skinType;
            powerMod = Mul(powerMod, 4915);
            modifiers.Add($"{attacker.Ability!.Name}({TypeNames.ToJapanese(skinType)}タイプ化 1.2倍)");
        }
        else if (attackerAbility == "normalize" && moveType != "Normal")
        {
            moveType = "Normal";
            powerMod = Mul(powerMod, 4915);
            modifiers.Add($"{attacker.Ability!.Name}(ノーマルタイプ化 1.2倍)");
        }

        var attackerGrounded = IsGrounded(attacker, attackerAbility);
        var defenderGrounded = IsGrounded(defender, defenderAbility);

        // --- 相性
        var defenderTypes = defender.DefensiveTypes;
        var effectiveness = 1.0;
        foreach (var t in defenderTypes)
            effectiveness *= _typeChart.Against(moveType, t);
        if (moveType == "Ground" && !defenderGrounded && effectiveness > 0 && !defenderTypes.Contains("Flying"))
            effectiveness = 0; // ふゆう・ふうせん

        // --- 特性による無効化
        var immunity = AbilityEffects.ImmunityReason(defenderAbility, moveType, move, effectiveness);
        if (immunity is not null)
            return Empty(defenderHp, 0, moveType, $"{defender.Ability!.Name}({immunity})");
        if (effectiveness == 0)
            return Empty(defenderHp, 0, moveType, "効果なし");

        // --- 威力
        var power = move.Power;
        if (attacker.IsDynamax)
        {
            power = MaxMovePower(move);
            modifiers.Add($"ダイマックス技(威力{power})");
        }
        var isTeraStab = attacker.IsTerastallized && attacker.TeraType == moveType;
        if (isTeraStab && power < 60 && move.Priority == 0)
        {
            power = 60;
            modifiers.Add("テラスタル(威力60保証)");
        }
        powerMod = ApplyPowerModifiers(request, attacker, defender, move, moveType, attackerAbility, defenderAbility,
            attackerGrounded, defenderGrounded, powerMod, modifiers);
        power = Math.Max(1, PokeRound(power * powerMod / 4096.0));

        // --- 攻撃側の実数値
        var attackStat = StatCalculator.Calculate(attacker, attackStatKind);
        var attackBoost = attacker.Boosts[attackStatKind];
        if (request.IsCritical && attackBoost < 0) attackBoost = 0;
        if (defenderAbility == "unaware") attackBoost = 0;
        attackStat = StatCalculator.ApplyBoost(attackStat, attackBoost);
        if (attackBoost != 0) modifiers.Add($"攻撃側ランク{(attackBoost > 0 ? "+" : "")}{attackBoost}");

        var attackMod = AttackModifier(request, attacker, defender, moveType, isPhysical, attackerAbility, defenderAbility, modifiers);
        if (attackMod != 4096) attackStat = Math.Max(1, PokeRound(attackStat * attackMod / 4096.0));

        // --- 防御側の実数値
        var defenseStat = StatCalculator.Calculate(defender, defenseStatKind);
        var defenseBoost = defender.Boosts[defenseStatKind];
        if (request.IsCritical && defenseBoost > 0) defenseBoost = 0;
        if (attackerAbility == "unaware") defenseBoost = 0;
        defenseStat = StatCalculator.ApplyBoost(defenseStat, defenseBoost);
        if (defenseBoost != 0) modifiers.Add($"防御側ランク{(defenseBoost > 0 ? "+" : "")}{defenseBoost}");

        if (request.Weather == Weather.Sand && !isPhysical && defenderTypes.Contains("Rock"))
        {
            defenseStat = PokeRound(defenseStat * 1.5);
            modifiers.Add("砂嵐(いわタイプの特防1.5倍)");
        }
        if (request.Weather == Weather.Snow && isPhysical && defenderTypes.Contains("Ice"))
        {
            defenseStat = PokeRound(defenseStat * 1.5);
            modifiers.Add("雪(こおりタイプの防御1.5倍)");
        }

        var defenseMod = DefenseModifier(request, defender, isPhysical, defenderAbility, modifiers);
        if (defenseMod != 4096) defenseStat = Math.Max(1, PokeRound(defenseStat * defenseMod / 4096.0));

        // --- 基礎ダメージ
        var levelTerm = attacker.Level * 2 / 5 + 2;
        var baseDamage = (levelTerm * power * attackStat / defenseStat) / 50 + 2;

        var target = move.Target;
        if (move.Id == MoveIdExpandingForce && request.Terrain == Terrain.Psychic && attackerGrounded)
            target = MoveTarget.AllFoes;
        if (request.Format == BattleFormat.Doubles && target != MoveTarget.Single)
        {
            baseDamage = PokeRound(baseDamage * 0.75);
            modifiers.Add("複数対象(0.75倍)");
        }

        // --- 天候
        if (request.Weather == Weather.Sun && moveType == "Fire" || request.Weather == Weather.Rain && moveType == "Water")
        {
            baseDamage = PokeRound(baseDamage * 1.5);
            modifiers.Add($"{FieldNames.Japanese(request.Weather)}(1.5倍)");
        }
        else if (request.Weather == Weather.Sun && moveType == "Water" || request.Weather == Weather.Rain && moveType == "Fire")
        {
            baseDamage = PokeRound(baseDamage * 0.5);
            modifiers.Add($"{FieldNames.Japanese(request.Weather)}(0.5倍)");
        }

        // --- 急所
        if (request.IsCritical)
        {
            var critMod = attackerAbility == "sniper" ? 2.25 : 1.5;
            baseDamage = PokeRound(baseDamage * critMod);
            modifiers.Add($"急所({critMod:0.##}倍)");
        }

        // --- タイプ一致
        var isOriginalStab = attacker.Pokemon.HasType(moveType);
        var stabMod = 4096;
        var adaptability = attackerAbility == "adaptability";
        if (isTeraStab && isOriginalStab)
            stabMod = adaptability ? 9216 : 8192;       // 2.25 / 2.0
        else if (isTeraStab || isOriginalStab)
            stabMod = adaptability ? 8192 : 6144;       // 2.0 / 1.5
        var isStab = stabMod != 4096;
        if (isStab) modifiers.Add($"タイプ一致({stabMod / 4096.0:0.##}倍)");

        // --- 最終補正（2 発目以降はマルチスケイル・半減きのみが無くなる）
        var finalMod = FinalModifier(request, attacker, defender, move, moveType, isPhysical, effectiveness,
            attackerAbility, defenderAbility, modifiers, firstHit: true);
        var laterMod = FinalModifier(request, attacker, defender, move, moveType, isPhysical, effectiveness,
            attackerAbility, defenderAbility, new List<string>(), firstHit: false);

        if (effectiveness != 1.0) modifiers.Add($"タイプ相性{effectiveness:0.##}倍");

        int[] ComputeRolls(int fm)
        {
            var result = new int[16];
            for (var i = 0; i < 16; i++)
            {
                var damage = baseDamage * (85 + i) / 100;
                if (isStab) damage = PokeRound(damage * stabMod / 4096.0);
                damage = (int)Math.Floor(damage * effectiveness);
                damage = PokeRound(damage * fm / 4096.0);
                if (damage < 1) damage = 1;
                result[i] = damage;
            }
            return result;
        }
        var rolls = ComputeRolls(finalMod);
        var laterRolls = laterMod == finalMod ? rolls : ComputeRolls(laterMod);
        var koOptions = KnockOutOptions.For(request, defender, defenderHp, defenderGrounded, laterRolls, modifiers);

        return new DamageResult
        {
            Rolls = rolls,
            DefenderHP = defenderHp,
            TypeEffectiveness = effectiveness,
            IsStab = isStab,
            AttackStat = attackStat,
            DefenseStat = defenseStat,
            BasePower = power,
            MoveType = moveType,
            KnockOut = KnockOutCalculator.Calculate(rolls, defenderHp, koOptions),
            Modifiers = modifiers,
        };
    }

    /// <summary>
    /// 技を、<paramref name="template"/> の条件（攻撃側・防御側・天候・フィールドなど）で与える最大ダメージの多い順に並べる。
    /// 同じダメージなら元の並び順のまま。変化技や効果が無い技は最後。
    /// </summary>
    public IReadOnlyList<Move> SortByDamage(DamageRequest template, IEnumerable<Move> moves) =>
        moves
            .Select(m => (Move: m, Damage: m.IsDamaging ? Calculate(template.With(move: m)).MaxDamage : -1))
            .OrderByDescending(x => x.Damage)
            .Select(x => x.Move)
            .ToList();

    /// <summary>防御側を決めていないときの仮の相手（タイプなし・種族値すべて 100・努力値なし）。物理と特殊を公平に比べるため。</summary>
    public static PokemonBuild NeutralDefender(EvSystem evSystem) =>
        new(new Pokemon { Name = "（防御側なし）", BaseStats = new StatSet(100, 100, 100, 100, 100, 100) }, evSystem);

    private static DamageResult Empty(int defenderHp, double effectiveness, string moveType, string reason) => new()
    {
        Rolls = new int[16],
        DefenderHP = defenderHp,
        TypeEffectiveness = effectiveness,
        IsStab = false,
        AttackStat = 0,
        DefenseStat = 0,
        BasePower = 0,
        MoveType = moveType,
        KnockOut = new KnockOut(0, 0),
        Modifiers = new[] { reason },
    };

    private static bool IsGrounded(PokemonBuild build, string ability)
    {
        if (ability == "levitate") return false;
        if (build.Item?.Id == ItemIdAirBalloon) return false;
        return !build.DefensiveTypes.Contains("Flying");
    }

    private static int ApplyPowerModifiers(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, Move move,
        string moveType, string attackerAbility, string defenderAbility, bool attackerGrounded, bool defenderGrounded,
        int mod, List<string> modifiers)
    {
        void Apply(int value, string text)
        {
            mod = Mul(mod, value);
            modifiers.Add(text);
        }

        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";

        // からげんき: 状態異常中は威力 2 倍
        if (move.Id == MoveIdFacade && attacker.Status is StatusCondition.Burn or StatusCondition.Poison or StatusCondition.BadlyPoisoned or StatusCondition.Paralysis)
            Apply(8192, "からげんき(状態異常で2倍)");

        // 攻撃側の特性
        switch (attackerAbility)
        {
            case "technician" when move.Power <= 60: Apply(6144, $"{atkName}(1.5倍)"); break;
            case "sheer-force" when move.EffectChance > 0: Apply(5325, $"{atkName}(1.3倍)"); break;
            case "iron-fist" when move.HasFlag("Punch"): Apply(4915, $"{atkName}(1.2倍)"); break;
            case "strong-jaw" when move.HasFlag("Bite"): Apply(6144, $"{atkName}(1.5倍)"); break;
            case "mega-launcher" when move.HasFlag("Pulse"): Apply(6144, $"{atkName}(1.5倍)"); break;
            case "tough-claws" when move.IsContact: Apply(5325, $"{atkName}(1.3倍)"); break;
            case "punk-rock" when move.IsSound: Apply(5325, $"{atkName}(1.3倍)"); break;
            case "sand-force" when request.Weather == Weather.Sand && moveType is "Rock" or "Ground" or "Steel": Apply(5325, $"{atkName}(1.3倍)"); break;
            case "steelworker" or "steely-spirit" when moveType == "Steel": Apply(6144, $"{atkName}(1.5倍)"); break;
            case "transistor" when moveType == "Electric": Apply(5325, $"{atkName}(1.3倍)"); break;
            case "dragons-maw" when moveType == "Dragon": Apply(6144, $"{atkName}(1.5倍)"); break;
            case "rocky-payload" when moveType == "Rock": Apply(6144, $"{atkName}(1.5倍)"); break;
            case "water-bubble" when moveType == "Water": Apply(8192, $"{atkName}(2倍)"); break;
            case "sharpness" when move.HasFlag("Slicing"): Apply(6144, $"{atkName}(1.5倍)"); break;
            case "reckless" when move.HasFlag("Recoil"): Apply(4915, $"{atkName}(1.2倍)"); break;
        }

        // 防御側の特性（威力に掛かるもの）
        switch (defenderAbility)
        {
            case "heatproof" when moveType == "Fire": Apply(2048, $"{defName}(0.5倍)"); break;
            case "dry-skin" when moveType == "Fire": Apply(5120, $"{defName}(1.25倍)"); break;
            case "fluffy" when move.IsContact && moveType != "Fire": Apply(2048, $"{defName}(接触技0.5倍)"); break;
            case "fluffy" when moveType == "Fire" && !move.IsContact: Apply(8192, $"{defName}(ほのお技2倍)"); break;
        }

        // 持ち物
        if (attacker.Item is { } item)
        {
            if (!string.IsNullOrEmpty(item.TypeBoost) && (item.TypeBoost == moveType || item.TypeBoost == move.Category.ToString()))
                Apply(PokeRound(4096 * item.TypeBoostMultiplier), $"{item.Name}(威力{item.TypeBoostMultiplier:0.##}倍)");
            else if (item.Id == ItemIdPunchingGlove && move.HasFlag("Punch"))
                Apply(4506, $"{item.Name}(1.1倍)");
        }

        // フィールド
        switch (request.Terrain)
        {
            case Terrain.Electric when attackerGrounded && moveType == "Electric":
                Apply(5325, "エレキフィールド(1.3倍)"); break;
            case Terrain.Grassy when attackerGrounded && moveType == "Grass":
                Apply(5325, "グラスフィールド(1.3倍)"); break;
            case Terrain.Psychic when attackerGrounded && moveType == "Psychic":
                Apply(5325, "サイコフィールド(1.3倍)"); break;
            case Terrain.Misty when defenderGrounded && moveType == "Dragon":
                Apply(2048, "ミストフィールド(ドラゴン技0.5倍)"); break;
        }
        if (request.Terrain == Terrain.Grassy && defenderGrounded && move.Id is MoveIdEarthquake or MoveIdMagnitude or MoveIdBulldoze)
            Apply(2048, "グラスフィールド(じしん系0.5倍)");
        if (request.Terrain == Terrain.Psychic && attackerGrounded && move.Id == MoveIdExpandingForce)
            Apply(6144, "ワイドフォース(サイコフィールドで1.5倍・全体)");
        if (request.Terrain == Terrain.Electric && defenderGrounded && move.Id == MoveIdRisingVoltage)
            Apply(8192, "ライジングボルト(エレキフィールドで2倍)");
        if (request.Terrain == Terrain.Electric && attackerGrounded && move.Id == MoveIdPsyblade)
            Apply(6144, "サイコブレイド(エレキフィールドで1.5倍)");

        return mod;
    }

    private static int AttackModifier(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, string moveType,
        bool isPhysical, string attackerAbility, string defenderAbility, List<string> modifiers)
    {
        var mod = 4096;
        void Apply(int value, string text)
        {
            mod = Mul(mod, value);
            modifiers.Add(text);
        }

        var atkName = attacker.Ability?.Name ?? "";
        var statName = isPhysical ? "攻撃" : "特攻";
        // もうか・しんりょく・げきりゅう・むしのしらせ: HP 1/3 以下で対応タイプの技の攻撃・特攻 1.5 倍
        var pinch = attacker.CurrentHp * 3 <= attacker.MaxHp;
        switch (attackerAbility)
        {
            case "blaze" when pinch && moveType == "Fire":
            case "overgrow" when pinch && moveType == "Grass":
            case "torrent" when pinch && moveType == "Water":
            case "swarm" when pinch && moveType == "Bug":
                Apply(6144, $"{atkName}(HP1/3以下 1.5倍)");
                break;
            case "huge-power" or "pure-power" when isPhysical: Apply(8192, $"{atkName}({statName}2倍)"); break;
            case "hustle" when isPhysical: Apply(6144, $"{atkName}({statName}1.5倍)"); break;
            case "gorilla-tactics" when isPhysical: Apply(6144, $"{atkName}({statName}1.5倍)"); break;
            case "solar-power" when !isPhysical && request.Weather == Weather.Sun: Apply(6144, $"{atkName}({statName}1.5倍)"); break;
            case "orichalcum-pulse" when isPhysical && request.Weather == Weather.Sun: Apply(5461, $"{atkName}({statName}1.33倍)"); break;
            case "hadron-engine" when !isPhysical && request.Terrain == Terrain.Electric: Apply(5461, $"{atkName}({statName}1.33倍)"); break;
            case "protosynthesis" or "quark-drive":
                if (ParadoxBoostActive(request, attacker, attackerAbility) &&
                    HighestStat(attacker) == (isPhysical ? Stat.Attack : Stat.SpAttack))
                    Apply(5325, $"{atkName}({statName}1.3倍)");
                break;
            case "guts" when isPhysical && attacker.Status != StatusCondition.None:
                Apply(6144, $"{atkName}(状態異常で{statName}1.5倍)"); break;
            case "defeatist" or "slow-start":
                break; // HP 依存のため未対応
        }

        // 防御側の特性で攻撃側の能力が下がるもの
        if (defenderAbility == "thick-fat" && moveType is "Fire" or "Ice")
            Apply(2048, $"{defender.Ability!.Name}({statName}0.5倍)");
        if (defenderAbility == "water-bubble" && moveType == "Fire")
            Apply(2048, $"{defender.Ability!.Name}(ほのお技0.5倍)");
        if (defenderAbility == "purifying-salt" && moveType == "Ghost")
            Apply(2048, $"{defender.Ability!.Name}(ゴースト技0.5倍)");

        // 持ち物
        if (attacker.Item is { } item)
        {
            switch (item.Category)
            {
                case ItemCategory.LightBall when attacker.Pokemon.SpeciesId == 25:
                    Apply(8192, $"{item.Name}(2倍)"); break;
                case ItemCategory.ThickClub when isPhysical && attacker.Pokemon.SpeciesId is 104 or 105:
                    Apply(8192, $"{item.Name}(2倍)"); break;
                default:
                    var itemMod = isPhysical ? item.AttackMultiplier : item.SpAttackMultiplier;
                    if (itemMod != 1.0) Apply(PokeRound(4096 * itemMod), $"{item.Name}({statName}{itemMod:0.##}倍)");
                    break;
            }
        }
        return mod;
    }

    private static int DefenseModifier(DamageRequest request, PokemonBuild defender, bool isPhysical, string defenderAbility, List<string> modifiers)
    {
        var mod = 4096;
        void Apply(int value, string text)
        {
            mod = Mul(mod, value);
            modifiers.Add(text);
        }

        var defName = defender.Ability?.Name ?? "";
        var statName = isPhysical ? "防御" : "特防";
        switch (defenderAbility)
        {
            case "fur-coat" when isPhysical: Apply(8192, $"{defName}({statName}2倍)"); break;
            case "grass-pelt" when isPhysical && request.Terrain == Terrain.Grassy: Apply(6144, $"{defName}({statName}1.5倍)"); break;
            case "protosynthesis" or "quark-drive":
                if (ParadoxBoostActive(request, defender, defenderAbility) &&
                    HighestStat(defender) == (isPhysical ? Stat.Defense : Stat.SpDefense))
                    Apply(5325, $"{defName}({statName}1.3倍)");
                break;
        }

        if (defender.Item is { } item)
        {
            if (item.Category == ItemCategory.Eviolite)
            {
                if (defender.Pokemon.NotFullyEvolved) Apply(6144, $"{item.Name}({statName}1.5倍)");
            }
            else
            {
                var itemMod = isPhysical ? item.DefenseMultiplier : item.SpDefenseMultiplier;
                if (itemMod != 1.0) Apply(PokeRound(4096 * itemMod), $"{item.Name}({statName}{itemMod:0.##}倍)");
            }
        }
        return mod;
    }

    // firstHit: 1 発目なら true。マルチスケイル（満タン時のみ）と半減きのみ（1 回だけ）は 1 発目にだけ効く。
    private static int FinalModifier(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, Move move, string moveType, bool isPhysical,
        double effectiveness, string attackerAbility, string defenderAbility, List<string> modifiers, bool firstHit)
    {
        var mod = 4096;
        void Apply(int value, string text)
        {
            mod = Mul(mod, value);
            modifiers.Add(text);
        }

        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";

        // 壁: 急所とすりぬけは無視。ダブルは 2732/4096
        var physicalSide = isPhysical || move.UsesPhysicalDefense;
        if (request.HasScreenFor(physicalSide) && !request.IsCritical && attackerAbility != "infiltrator")
        {
            var wall = physicalSide ? "リフレクター" : "ひかりのかべ";
            if (request.Format == BattleFormat.Doubles) Apply(2732, $"{wall}(ダブル 0.67倍)");
            else Apply(2048, $"{wall}(0.5倍)");
        }

        // やけど: 物理技 0.5 倍（こんじょう・からげんきは除く）
        if (attacker.Status == StatusCondition.Burn && isPhysical && attackerAbility != "guts" && move.Id != MoveIdFacade)
            Apply(2048, "やけど(物理0.5倍)");

        if (attackerAbility == "tinted-lens" && effectiveness < 1) Apply(8192, $"{atkName}(2倍)");
        if (attackerAbility == "neuroforce" && effectiveness > 1) Apply(5120, $"{atkName}(1.25倍)");

        switch (defenderAbility)
        {
            case "multiscale" or "shadow-shield" when firstHit && defender.IsFullHp: Apply(2048, $"{defName}(HP満タン 0.5倍)"); break;
            case "filter" or "solid-rock" or "prism-armor" when effectiveness > 1: Apply(3072, $"{defName}(0.75倍)"); break;
            case "ice-scales" when !isPhysical: Apply(2048, $"{defName}(特殊技0.5倍)"); break;
            case "punk-rock" when move.IsSound: Apply(2048, $"{defName}(音技0.5倍)"); break;
        }

        if (attacker.Item is { } item)
        {
            if (item.Category == ItemCategory.LifeOrb) Apply(5324, $"{item.Name}(1.3倍)");
            else if (item.Category == ItemCategory.ExpertBelt && effectiveness > 1) Apply(4915, $"{item.Name}(1.2倍)");
            else if (item.Category is not (ItemCategory.LifeOrb or ItemCategory.ExpertBelt) && item.DamageMultiplier != 1.0)
                Apply(PokeRound(4096 * item.DamageMultiplier), $"{item.Name}({item.DamageMultiplier:0.##}倍)");
        }

        // 半減きのみ（1 回だけ）。ホズのみはノーマル技なら相性に関係なく半減
        if (firstHit && defender.Item is { Category: ItemCategory.ResistBerry } berry && berry.TypeBoost == moveType
            && (effectiveness > 1 || moveType == "Normal"))
            Apply(2048, $"{berry.Name}(1発目のみ 0.5倍)");
        return mod;
    }

    private static bool ParadoxBoostActive(DamageRequest request, PokemonBuild build, string ability)
    {
        if (build.Item?.Id == ItemIdBoosterEnergy) return true;
        return ability == "protosynthesis" ? request.Weather == Weather.Sun : request.Terrain == Terrain.Electric;
    }

    /// <summary>ランク込みで一番高い能力（こだいかっせい・クォークチャージで上がる能力）。</summary>
    public static Stat HighestStat(PokemonBuild build)
    {
        var stats = StatCalculator.CalculateAll(build);
        var best = Stat.Attack;
        foreach (var stat in new[] { Stat.Attack, Stat.Defense, Stat.SpAttack, Stat.SpDefense, Stat.Speed })
        {
            if (StatCalculator.ApplyBoost(stats[stat], build.Boosts[stat]) > StatCalculator.ApplyBoost(stats[best], build.Boosts[best]))
                best = stat;
        }
        return best;
    }

    /// <summary>4096 基準の補正値同士を掛ける（四捨五入）。</summary>
    private static int Mul(int a, int b) => (int)Math.Round(a * (long)b / 4096.0, MidpointRounding.AwayFromZero);

    /// <summary>五捨五超入（0.5 ちょうどは切り捨て）。</summary>
    public static int PokeRound(double value)
    {
        var floor = Math.Floor(value);
        return value - floor > 0.5 ? (int)floor + 1 : (int)floor;
    }
}

/// <summary>特性の分類テーブル。識別子は PokeAPI のもの。</summary>
public static class AbilityEffects
{
    public static bool IgnoresTargetAbility(string ability) =>
        ability is "mold-breaker" or "teravolt" or "turboblaze" or "mycelium-might";

    /// <summary>ノーマル技を別タイプに変える特性。</summary>
    public static string? SkinType(string ability) => ability switch
    {
        "pixilate" => "Fairy",
        "aerilate" => "Flying",
        "refrigerate" => "Ice",
        "galvanize" => "Electric",
        _ => null,
    };

    /// <summary>防御側の特性で技が無効になる場合、その理由。無効でなければ null。</summary>
    public static string? ImmunityReason(string ability, string moveType, Move move, double effectiveness) => ability switch
    {
        "levitate" or "earth-eater" when moveType == "Ground" => "じめん技無効",
        "water-absorb" or "storm-drain" or "dry-skin" when moveType == "Water" => "みず技無効",
        "flash-fire" or "well-baked-body" when moveType == "Fire" => "ほのお技無効",
        "volt-absorb" or "lightning-rod" or "motor-drive" when moveType == "Electric" => "でんき技無効",
        "sap-sipper" when moveType == "Grass" => "くさ技無効",
        "soundproof" when move.IsSound => "音技無効",
        "bulletproof" when move.HasFlag("Bullet") => "弾技無効",
        "wonder-guard" when effectiveness <= 1 => "効果抜群以外無効",
        _ => null,
    };

    /// <summary>
    /// 攻撃側として持っていると、この計算機でダメージが上がる特性（条件付きのものも含む）。
    /// ピンチ特性（もうか等）・こんじょう・スナイパーのように、状態異常・HP・急所が揃わないと効かないものは含めない。
    /// </summary>
    public static bool IsOffensive(string? ability) => ability is
        "huge-power" or "pure-power" or "adaptability" or "technician" or "sheer-force" or "tough-claws" or
        "pixilate" or "aerilate" or "refrigerate" or "galvanize" or "iron-fist" or "strong-jaw" or "mega-launcher" or
        "punk-rock" or "sand-force" or "steelworker" or "steely-spirit" or "transistor" or "dragons-maw" or
        "rocky-payload" or "water-bubble" or "sharpness" or "reckless" or "hustle" or "gorilla-tactics" or
        "solar-power" or "orichalcum-pulse" or "hadron-engine" or "protosynthesis" or "quark-drive" or
        "tinted-lens" or "neuroforce";

    /// <summary>場に出ると天候・フィールドを作る特性（ひでり・サイコメイカーなど）。</summary>
    public static bool SetsField(string? ability) =>
        FieldEffects.WeatherFromAbility(ability) is not null || FieldEffects.TerrainFromAbility(ability) is not null;

    /// <summary>既定の特性を選ぶときの立場。</summary>
    public enum Side
    {
        /// <summary>攻撃側（ダメージが上がる特性を優先）。</summary>
        Attacker,
        /// <summary>防御側（ちょすい・あついしぼうなど受けで効く特性を優先）。</summary>
        Defender,
        /// <summary>どちらにもなる（タイプ相性表など）。使用率 1 位が攻撃・受けどちらかで効く特性ならそのまま使う。</summary>
        Either,
    }

    /// <summary>
    /// ポケモンを選んだときの既定の特性。天候・フィールドを作る特性か、<paramref name="side"/> で効く特性
    /// （攻撃側はダメージが上がる特性、防御側は受けで効く特性）を持っていればそれを、
    /// 無ければ使用率 1 位（<paramref name="usageTop"/>。無ければ第 1 特性）を選ぶ。
    /// 使用率 1 位がもともとそういう特性ならそのまま使う（防御側のトリトドンはよびみずのまま）。
    /// </summary>
    public static Ability? ChooseDefault(IReadOnlyList<Ability> abilities, Ability? usageTop, Side side = Side.Attacker)
    {
        bool Offensive(Ability a) => side != Side.Defender && IsOffensive(a.Identifier);
        bool Defensive(Ability a) => side != Side.Attacker && IsDefensive(a.Identifier);
        if (usageTop is not null && (SetsField(usageTop.Identifier) || Offensive(usageTop) || Defensive(usageTop))) return usageTop;
        // 天候・フィールドを作る特性を優先（技の並び順や場の状態にも効くため）
        return abilities.FirstOrDefault(a => SetsField(a.Identifier))
            ?? abilities.FirstOrDefault(Offensive)
            ?? abilities.FirstOrDefault(Defensive)
            ?? usageTop
            ?? abilities.FirstOrDefault();
    }

    /// <summary>防御側に付くのが自然な特性（チャットコマンドの振り分けに使う）。</summary>
    public static bool IsDefensive(string ability) => ability is
        "levitate" or "earth-eater" or "water-absorb" or "storm-drain" or "dry-skin" or "flash-fire" or "well-baked-body" or
        "volt-absorb" or "lightning-rod" or "motor-drive" or "sap-sipper" or "soundproof" or "bulletproof" or "wonder-guard" or
        "multiscale" or "shadow-shield" or "filter" or "solid-rock" or "prism-armor" or "ice-scales" or "fur-coat" or
        "grass-pelt" or "thick-fat" or "heatproof" or "fluffy" or "purifying-salt" or "unaware" or "marvel-scale";
}

/// <summary>乱数 16 通りから確定数と確率を求める。防御側の状態異常による定数ダメージ（やけど・どく・もうどく）を織り込む。回復は考慮しない。</summary>
/// <summary>確定数の計算条件（開始 HP・定数ダメージ・回復）。</summary>
public sealed class KnockOutOptions
{
    /// <summary>計算開始時の HP（0 なら最大 HP）。</summary>
    public int StartHp { get; init; }
    public StatusCondition Status { get; init; }
    /// <summary>2 発目以降の乱数（マルチスケイル・半減きのみが消えた後）。null なら 1 発目と同じ。</summary>
    public int[]? LaterRolls { get; init; }
    /// <summary>毎ターン終了時の HP 増減（たべのこし +1/16、くろいヘドロ -1/8 など）。</summary>
    public int EndOfTurnHeal { get; init; }
    /// <summary>回復きのみが発動する HP（この値以下）。0 ならきのみなし。</summary>
    public int BerryThreshold { get; init; }
    public int BerryHeal { get; init; }

    public static KnockOutOptions Default { get; } = new();

    /// <summary>防御側の持ち物・状態・フィールドから条件を組み立て、反映した内容を modifiers に書き足す。</summary>
    public static KnockOutOptions For(DamageRequest request, PokemonBuild defender, int maxHp, bool grounded, int[] laterRolls, List<string> modifiers)
    {
        var start = Math.Min(maxHp, defender.CurrentHp);
        if (start < maxHp) modifiers.Add($"防御側の残りHP {start}/{maxHp}");
        var eot = 0;
        var threshold = 0;
        var heal = 0;
        var sixteenth = Math.Max(1, maxHp / 16);
        if (request.Terrain == Terrain.Grassy && grounded)
        {
            eot += sixteenth;
            modifiers.Add("グラスフィールド(毎ターン1/16回復)を確定数に反映");
        }
        switch (defender.Item)
        {
            case { Category: ItemCategory.Leftovers } item:
                eot += sixteenth;
                modifiers.Add($"{item.Name}(毎ターン1/16回復)を確定数に反映");
                break;
            case { Category: ItemCategory.BlackSludge } item when defender.DefensiveTypes.Contains("Poison") || defender.Pokemon.HasType("Poison"):
                eot += sixteenth;
                modifiers.Add($"{item.Name}(毎ターン1/16回復)を確定数に反映");
                break;
            case { Category: ItemCategory.BlackSludge } item:
                eot -= Math.Max(1, maxHp / 8);
                modifiers.Add($"{item.Name}(毎ターン1/8ダメージ)を確定数に反映");
                break;
            case { Category: ItemCategory.SitrusBerry } item:
                threshold = maxHp / 2;
                heal = Math.Max(1, maxHp / 4);
                modifiers.Add($"{item.Name}(HP1/2以下で1/4回復)を確定数に反映");
                break;
            case { Category: ItemCategory.OranBerry } item:
                threshold = maxHp / 2;
                heal = 10;
                modifiers.Add($"{item.Name}(HP1/2以下で10回復)を確定数に反映");
                break;
            case { Category: ItemCategory.PinchBerry } item:
                threshold = maxHp / 4;
                heal = Math.Max(1, maxHp / 3);
                modifiers.Add($"{item.Name}(HP1/4以下で1/3回復)を確定数に反映");
                break;
        }
        return new KnockOutOptions
        {
            StartHp = start,
            Status = defender.Status,
            LaterRolls = laterRolls,
            EndOfTurnHeal = eot,
            BerryThreshold = threshold,
            BerryHeal = heal,
        };
    }
}

public static class KnockOutCalculator
{
    public const int MaxHits = 10;

    public static KnockOut Calculate(int[] rolls, int hp) => Calculate(rolls, hp, KnockOutOptions.Default);

    public static KnockOut Calculate(int[] rolls, int hp, StatusCondition defenderStatus) =>
        Calculate(rolls, hp, new KnockOutOptions { Status = defenderStatus });

    /// <summary>
    /// 何発で倒せるかと、その確率。残り HP（と回復きのみを使ったか）の分布を 1 発ずつ進めて厳密に求める。
    /// 1 発ごとに「攻撃 → きのみ → ターン終了時の回復 → 状態異常の定数ダメージ → きのみ」の順で進める。
    /// </summary>
    public static KnockOut Calculate(int[] rolls, int hp, KnockOutOptions options)
    {
        if (rolls.Length == 0 || rolls[^1] <= 0 || hp <= 0)
            return new KnockOut(0, 0);

        var start = options.StartHp > 0 ? Math.Min(options.StartHp, hp) : hp;
        // キー: 残り HP * 2 + (きのみを使ったら 1)
        var dist = new Dictionary<int, double> { [start * 2] = 1.0 };
        var knockedOut = 0.0;
        var perRoll = 1.0 / rolls.Length;
        for (var hits = 1; hits <= MaxHits; hits++)
        {
            var hitRolls = hits == 1 ? rolls : options.LaterRolls ?? rolls;
            var residual = StatusNames.ResidualDamage(options.Status, hp, hits);
            var next = new Dictionary<int, double>();
            foreach (var (key, p) in dist)
            {
                var remaining = key / 2;
                var usedBerry = (key & 1) == 1;
                foreach (var roll in hitRolls)
                {
                    var r = remaining - roll;
                    var used = usedBerry;
                    if (r <= 0) { knockedOut += p * perRoll; continue; }
                    TryBerry(ref r, ref used);
                    r = Math.Min(hp, r + options.EndOfTurnHeal);
                    if (r <= 0) { knockedOut += p * perRoll; continue; }
                    r -= residual;
                    if (r <= 0) { knockedOut += p * perRoll; continue; }
                    TryBerry(ref r, ref used);
                    var k = r * 2 + (used ? 1 : 0);
                    next[k] = next.GetValueOrDefault(k) + p * perRoll;
                }
            }
            dist = next;
            if (knockedOut > 1e-9)
                return new KnockOut(hits, Math.Min(1.0, knockedOut));
        }
        return new KnockOut(MaxHits + 1, 0);

        void TryBerry(ref int r, ref bool used)
        {
            if (used || options.BerryThreshold <= 0 || r > options.BerryThreshold) return;
            r = Math.Min(hp, r + options.BerryHeal);
            used = true;
        }
    }
}
