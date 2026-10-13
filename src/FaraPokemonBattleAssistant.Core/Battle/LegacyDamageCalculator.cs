using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Battle;

/// <summary>
/// 昔の世代（第 3〜7 世代）のダメージ計算。
/// 補正を掛ける順番・丸め方は、公開されているダメージ計算ツール（@smogon/calc の gen3 / gen4 / gen56 / gen789）に合わせる。
/// 第 3〜4 世代は LegacyDamageCalculator.Gen34.cs。
/// 第 8 世代以降は <see cref="DamageCalculator"/> の計算（今までの計算）をそのまま使う。
/// 対応している特性・持ち物・技の範囲は第 9 世代の計算と同じ（威力が変動する技の多くは未対応）。
/// </summary>
internal sealed partial class LegacyDamageCalculator
{
    private const int ItemIdAirBalloon = 584;
    private const int MoveIdEarthquake = 89;
    private const int MoveIdBulldoze = 523;
    private const int MoveIdFacade = 263;
    private const int MoveIdKnockOff = 282;
    private const int MoveIdSolarBeam = 76;
    private const int MoveIdSolarBlade = 669;
    private const int MoveIdBrickBreak = 280;
    private const int MoveIdPsychicFangs = 706;

    private readonly TypeChart _chart;
    private readonly int _gen;

    public LegacyDamageCalculator(TypeChart chart, int generation)
    {
        if (generation is < 3 or > 7) throw new ArgumentOutOfRangeException(nameof(generation), "第 3〜7 世代だけに対応しています");
        _chart = chart;
        _gen = generation;
    }

    /// <summary>かたやぶり系で無視される防御側の特性（この計算で効果を扱うもの）。</summary>
    private static readonly HashSet<string> IgnorableAbilities = new()
    {
        "battle-armor", "shell-armor", "bulletproof", "dry-skin", "filter", "flash-fire", "fur-coat", "grass-pelt",
        "heatproof", "levitate", "lightning-rod", "marvel-scale", "motor-drive", "multiscale", "sap-sipper",
        "solid-rock", "soundproof", "storm-drain", "thick-fat", "unaware", "volt-absorb", "water-absorb",
        "wonder-guard", "fluffy", "water-bubble",
    };

    public DamageResult Calculate(DamageRequest request)
    {
        if (_gen <= 4) return CalculateGen34(request);
        var attacker = request.Attacker;
        var defender = request.Defender;
        var move = request.Move;
        var modifiers = new List<string>();
        var defenderHp = defender.MaxHp;
        if (!move.IsDamaging)
            return Empty(defenderHp, 1.0, move.Type, "変化技");

        // Z 技（第 7 世代）: 威力は Z 技の表、接触・パンチなどの技の性質や追加効果は無くなる。持ち物は Z クリスタル
        var isZ = _gen == 7 && request.IsZMove;
        var attackerItem = isZ ? null : attacker.Item;
        var defenderItem = defender.Item;
        if (isZ) modifiers.Add($"Z技(威力{ZMove.Power(move)}・持ち物はZクリスタル)");

        var atkAbility = attacker.Ability?.Identifier ?? "";
        var defAbility = defender.Ability?.Identifier ?? "";
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";

        // ノーてんき・エアロック: 天候の効果が無くなる
        var weather = request.Weather;
        if (weather != Weather.None && (atkAbility is "air-lock" or "cloud-nine" || defAbility is "air-lock" or "cloud-nine"))
        {
            modifiers.Add($"{(atkAbility is "air-lock" or "cloud-nine" ? atkName : defName)}(天候の効果なし)");
            weather = Weather.None;
        }
        // フィールドは第 6 世代から（サイコフィールドは第 7 世代から）
        var terrain = _gen >= 6 && !(request.Terrain == Terrain.Psychic && _gen < 7) ? request.Terrain : Terrain.None;

        if (atkAbility is "mold-breaker" or "teravolt" or "turboblaze" && IgnorableAbilities.Contains(defAbility))
        {
            modifiers.Add($"{atkName}(相手の特性を無視)");
            defAbility = "";
        }

        var isCritical = request.IsCritical && defAbility is not ("battle-armor" or "shell-armor");
        if (request.IsCritical && !isCritical) modifiers.Add($"{defName}(急所に当たらない)");

        // --- 技タイプ（スキン系・ノーマルスキン）
        var moveType = move.Type;
        var hasAte = false;
        // ウェザーボールはスキン系特性でタイプが変わらない
        if (!isZ && move.Id != MoveEffects.MoveIdWeatherBall)
        {
            var skin = SkinType(atkAbility);
            if (skin is not null && moveType == "Normal")
            {
                moveType = skin;
                hasAte = true;
                modifiers.Add($"{atkName}({TypeNames.ToJapanese(skin)}タイプ化 {(_gen == 6 ? "1.3" : "1.2")}倍)");
            }
            else if (atkAbility == "normalize")
            {
                moveType = "Normal";
                hasAte = _gen >= 7; // 第 7 世代からノーマルスキンも 1.2 倍
                modifiers.Add(_gen >= 7 ? $"{atkName}(ノーマルタイプ化 1.2倍)" : $"{atkName}(ノーマルタイプ化)");
            }
        }

        // --- 壁（かわらわり・サイコファングは壁を壊す。すりぬけは無視）
        var reflect = request.Reflect || request.Screen;
        var lightScreen = request.LightScreen || request.Screen;
        if (!isZ && move.Id is MoveIdBrickBreak or MoveIdPsychicFangs) reflect = lightScreen = false;
        if (atkAbility == "infiltrator") reflect = lightScreen = false;

        // --- 相性
        var effectiveness = 1.0;
        foreach (var t in defender.Pokemon.Types)
        {
            var m = _chart.Against(moveType, t);
            if (m == 0 && t == "Ghost" && atkAbility == "scrappy" && moveType is "Normal" or "Fighting") m = 1;
            effectiveness *= m;
        }
        if (effectiveness == 0)
            return Empty(defenderHp, 0, moveType, "効果なし");

        var flags = isZ ? new HashSet<string>() : move.Flags;
        var immunity = defAbility switch
        {
            "wonder-guard" when effectiveness <= 1 => "効果抜群以外無効",
            "sap-sipper" when moveType == "Grass" => "くさ技無効",
            "flash-fire" when moveType == "Fire" => "ほのお技無効",
            "dry-skin" or "storm-drain" or "water-absorb" when moveType == "Water" => "みず技無効",
            "lightning-rod" or "motor-drive" or "volt-absorb" when moveType == "Electric" => "でんき技無効",
            "levitate" when moveType == "Ground" => "じめん技無効",
            "bulletproof" when flags.Contains("Bullet") => "弾技無効",
            "soundproof" when flags.Contains("Sound") => "音技無効",
            _ => null,
        };
        if (immunity is not null)
            return Empty(defenderHp, 0, moveType, $"{defName}({immunity})");
        if (moveType == "Ground" && defenderItem?.Id == ItemIdAirBalloon)
            return Empty(defenderHp, 0, moveType, $"{defenderItem.Name}(じめん技無効)");
        if (terrain == Terrain.Psychic && move.Priority > 0 && !isZ && IsGrounded(defender, defAbility, defenderItem))
            return Empty(defenderHp, 0, moveType, "サイコフィールド(先制技無効)");

        var isPhysical = move.Category == MoveCategory.Physical;
        var attackerGrounded = IsGrounded(attacker, atkAbility, attackerItem);
        var defenderGrounded = IsGrounded(defender, defAbility, defenderItem);

        // --- 威力
        var basePower = isZ ? ZMove.Power(move) : move.Power;
        var bpMods = _gen >= 7
            ? BasePowerModsGen7(request, attacker, defender, move, moveType, isZ, flags, hasAte, atkAbility, defAbility,
                attackerItem, defenderItem, basePower, terrain, weather, attackerGrounded, defenderGrounded, modifiers)
            : BasePowerModsGen56(request, attacker, defender, move, moveType, flags, hasAte, atkAbility, defAbility,
                attackerItem, defenderItem, basePower, terrain, weather, attackerGrounded, defenderGrounded, modifiers);
        basePower = Math.Max(1, DamageCalculator.PokeRound(basePower * (double)Chain(bpMods, 41, 2097152) / 4096));

        // --- 攻撃側の実数値（イカサマは相手の攻撃とランク）
        var attackStatKind = move.AttackStatUsed;
        var attackSource = !isZ && move.Id == MoveEffects.MoveIdFoulPlay ? defender : attacker;
        if (attackSource != attacker) modifiers.Add($"{move.Name}(相手のAで計算)");
        var attack = StatCalculator.Calculate(attackSource, attackStatKind);
        var attackBoost = attackSource.Boosts[attackStatKind];
        if (attackBoost != 0 && !(isCritical && attackBoost < 0))
        {
            if (defAbility == "unaware") modifiers.Add($"{defName}(ランク無視)");
            else
            {
                attack = StatCalculator.ApplyBoost(attack, attackBoost);
                modifiers.Add($"攻撃側ランク{(attackBoost > 0 ? "+" : "")}{attackBoost}");
            }
        }
        if (atkAbility == "hustle" && isPhysical)
        {
            attack = DamageCalculator.PokeRound(attack * 3 / 2.0);
            modifiers.Add($"{atkName}(A1.5倍)");
        }
        var atMods = _gen >= 7
            ? AttackModsGen7(attacker, move, moveType, isPhysical, isZ, atkAbility, defAbility, attackerItem, weather, modifiers)
            : AttackModsGen56(attacker, move, moveType, isPhysical, atkAbility, defAbility, attackerItem, weather, modifiers);
        attack = Math.Max(1, DamageCalculator.PokeRound(attack * (double)Chain(atMods, 410, 131072) / 4096));

        // --- 防御側の実数値
        // サイコショックなどは相手の防御で計算（Z 技にすると特防）
        var hitsPhysical = isPhysical || move.UsesPhysicalDefense && !isZ;
        var defenseStatKind = hitsPhysical ? Stat.Defense : Stat.SpDefense;
        if (move.UsesPhysicalDefense && !isZ) modifiers.Add($"{move.Name}(相手のBで計算)");
        var defense = StatCalculator.Calculate(defender, defenseStatKind);
        var defenseBoost = defender.Boosts[defenseStatKind];
        if (defenseBoost != 0 && !isZ && MoveEffects.IgnoresDefenseBoosts(move))
        {
            modifiers.Add($"{move.Name}(相手のランク変化を無視)");
            defenseBoost = 0;
        }
        if (defenseBoost != 0 && !(isCritical && defenseBoost > 0))
        {
            if (atkAbility == "unaware") modifiers.Add($"{atkName}(ランク無視)");
            else
            {
                defense = StatCalculator.ApplyBoost(defense, defenseBoost);
                modifiers.Add($"防御側ランク{(defenseBoost > 0 ? "+" : "")}{defenseBoost}");
            }
        }
        if (weather == Weather.Sand && defender.Pokemon.HasType("Rock") && !hitsPhysical)
        {
            defense = DamageCalculator.PokeRound(defense * 3 / 2.0);
            modifiers.Add("砂嵐(いわタイプのD1.5倍)");
        }
        var dfMods = _gen >= 7
            ? DefenseModsGen7(defender, hitsPhysical, defAbility, defenderItem, terrain, modifiers)
            : DefenseModsGen56(defender, hitsPhysical, defAbility, defenderItem, terrain, modifiers);
        defense = Math.Max(1, DamageCalculator.PokeRound(defense * (double)Chain(dfMods, 410, 131072) / 4096));

        // --- 基礎ダメージ
        var baseDamage = (attacker.Level * 2 / 5 + 2) * basePower * attack / defense / 50 + 2;
        var isSpread = request.Format == BattleFormat.Doubles && move.Target != MoveTarget.Single && !isZ;
        if (isSpread)
        {
            baseDamage = DamageCalculator.PokeRound(baseDamage * 3072 / 4096.0);
            modifiers.Add("複数対象(0.75倍)");
        }
        if (weather == Weather.Sun && moveType == "Fire" || weather == Weather.Rain && moveType == "Water")
        {
            baseDamage = DamageCalculator.PokeRound(baseDamage * 6144 / 4096.0);
            modifiers.Add($"{FieldNames.Japanese(weather)}(1.5倍)");
        }
        else if (weather == Weather.Sun && moveType == "Water" || weather == Weather.Rain && moveType == "Fire")
        {
            baseDamage = DamageCalculator.PokeRound(baseDamage * 2048 / 4096.0);
            modifiers.Add($"{FieldNames.Japanese(weather)}(0.5倍)");
        }
        if (isCritical)
        {
            var crit = _gen >= 6 ? 1.5 : 2.0;
            baseDamage = (int)Math.Floor(baseDamage * crit);
            modifiers.Add($"急所({crit:0.#}倍)");
        }

        // --- タイプ一致
        var stab = 4096;
        if (attacker.Pokemon.HasType(moveType)) stab += 2048;
        if (atkAbility == "adaptability" && attacker.Pokemon.HasType(moveType)) stab += 2048;
        if (stab != 4096) modifiers.Add($"タイプ一致({stab / 4096.0:0.##}倍)");

        // --- やけど（こんじょうは除く。からげんきは第 6 世代から除く）
        var applyBurn = attacker.Status == StatusCondition.Burn && isPhysical && atkAbility != "guts"
            && !(move.Id == MoveIdFacade && !isZ && _gen >= 6);
        if (applyBurn) modifiers.Add("やけど(物理0.5倍)");

        if (effectiveness != 1.0) modifiers.Add($"タイプ相性{effectiveness:0.##}倍");

        // --- 最終補正（2 発目以降はマルチスケイル・半減きのみが無くなる）
        var finalMods = FinalMods(request, attacker, defender, move, moveType, isPhysical, isZ, flags, effectiveness, isCritical,
            reflect, lightScreen, atkAbility, defAbility, attackerItem, defenderItem, modifiers, firstHit: true);
        var laterMods = FinalMods(request, attacker, defender, move, moveType, isPhysical, isZ, flags, effectiveness, isCritical,
            reflect, lightScreen, atkAbility, defAbility, attackerItem, defenderItem, new List<string>(), firstHit: false);
        var finalMod = Chain(finalMods, 41, 131072);
        var laterMod = Chain(laterMods, 41, 131072);

        int[] Rolls(int fm)
        {
            var result = new int[16];
            for (var i = 0; i < 16; i++)
                result[i] = FinalDamage(baseDamage, i, effectiveness, applyBurn, stab, fm);
            return result;
        }
        var rolls = Rolls(finalMod);
        var laterRolls = laterMod == finalMod ? rolls : Rolls(laterMod);
        var koOptions = KnockOutOptions.For(request, defender, defenderHp, defenderGrounded, laterRolls, modifiers, _gen);

        return new DamageResult
        {
            Rolls = rolls,
            LaterRolls = laterRolls,
            DefenderHP = defenderHp,
            TypeEffectiveness = effectiveness,
            IsStab = stab != 4096,
            AttackStat = attack,
            DefenseStat = defense,
            BasePower = basePower,
            MoveType = moveType,
            KnockOut = KnockOutCalculator.Calculate(rolls, defenderHp, koOptions),
            Modifiers = modifiers,
        };
    }

    /// <summary>乱数・タイプ一致・相性・やけど・最終補正（@smogon/calc の getFinalDamage と同じ）。</summary>
    internal static int FinalDamage(int baseDamage, int roll, double effectiveness, bool burn, int stab, int finalMod)
    {
        double damage = baseDamage * (85 + roll) / 100;
        if (stab != 4096) damage = damage * stab / 4096;
        var d = (int)Math.Floor(DamageCalculator.PokeRound(damage) * effectiveness);
        if (burn) d /= 2;
        return DamageCalculator.PokeRound(Math.Max(1, d * (double)finalMod / 4096));
    }

    /// <summary>4096 を 1 倍とした補正値を順に掛け合わせる（@smogon/calc の chainMods）。</summary>
    internal static int Chain(IEnumerable<int> mods, int lower, int upper)
    {
        long m = 4096;
        foreach (var mod in mods)
        {
            if (mod != 4096) m = (m * mod + 2048) >> 12;
        }
        return (int)Math.Max(Math.Min(m, upper), lower);
    }

    private string? SkinType(string ability) => ability switch
    {
        "pixilate" when _gen >= 6 => "Fairy",
        "aerilate" when _gen >= 6 => "Flying",
        "refrigerate" when _gen >= 6 => "Ice",
        "galvanize" when _gen >= 7 => "Electric",
        _ => null,
    };

    private static bool IsGrounded(PokemonBuild build, string ability, Item? item) =>
        !build.Pokemon.HasType("Flying") && ability != "levitate" && item?.Id != ItemIdAirBalloon;

    private static bool IsChoiceBand(Item? item) => item is { Category: ItemCategory.Choice, AttackMultiplier: > 1 };
    private static bool IsChoiceSpecs(Item? item) => item is { Category: ItemCategory.Choice, SpAttackMultiplier: > 1 };
    private static bool IsAssaultVest(Item? item) => item is { Category: ItemCategory.AssaultVest };
    private static bool IsMuscleBand(Item? item, bool physical) =>
        item is { Category: ItemCategory.Other, TypeBoost: "Physical" or "Special" } && item.TypeBoost == (physical ? "Physical" : "Special");
    private static bool IsTypeBoost(Item? item, string moveType) => item is { Category: ItemCategory.TypeBoost } && item.TypeBoost == moveType;

    private List<int> BasePowerModsGen56(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, Move move, string moveType,
        IReadOnlySet<string> flags, bool hasAte, string atkAbility, string defAbility, Item? attackerItem, Item? defenderItem, int basePower,
        Terrain terrain, Weather weather, bool attackerGrounded, bool defenderGrounded, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";

        if (atkAbility == "technician" && basePower <= 60) Add(6144, $"{atkName}(1.5倍)");
        else if (atkAbility == "sand-force" && weather == Weather.Sand && moveType is "Rock" or "Ground" or "Steel") Add(5325, $"{atkName}(1.3倍)");
        else if (atkAbility == "iron-fist" && flags.Contains("Punch") || atkAbility == "reckless" && MoveEffects.IsRecoil(move)) Add(4915, $"{atkName}(1.2倍)");

        if (defAbility == "heatproof" && moveType == "Fire") Add(2048, $"{defName}(0.5倍)");
        else if (defAbility == "dry-skin" && moveType == "Fire") Add(5120, $"{defName}(1.25倍)");

        if (atkAbility == "sheer-force" && MoveEffects.SheerForceBoosts(move, _gen)) Add(5325, $"{atkName}(1.3倍)");

        if (IsTypeBoost(attackerItem, moveType)) Add(4915, $"{attackerItem!.Name}(1.2倍)");
        else if (IsMuscleBand(attackerItem, move.Category == MoveCategory.Physical)) Add(4505, $"{attackerItem!.Name}(1.1倍)");

        if (move.Id == MoveIdFacade && attacker.Status is StatusCondition.Burn or StatusCondition.Paralysis or StatusCondition.Poison or StatusCondition.BadlyPoisoned)
            Add(8192, "からげんき(状態異常で2倍)");
        else if (move.Id == MoveEffects.MoveIdBrine && defender.CurrentHp * 2 <= defender.MaxHp)
            Add(8192, "しおみず(相手のHPが半分以下で2倍)");
        else if (move.Id == MoveEffects.MoveIdVenoshock && defender.Status is StatusCondition.Poison or StatusCondition.BadlyPoisoned)
            Add(8192, "ベノムショック(相手がどくで2倍)");
        else if (_gen > 5 && move.Id == MoveIdKnockOff && MoveEffects.KnockOffBoosted(defenderItem, defender)) Add(6144, "はたきおとす(持ち物があるので1.5倍)");
        else if (move.Id == MoveIdSolarBeam && weather is Weather.Rain or Weather.Sand or Weather.Snow) Add(2048, $"{move.Name}({FieldNames.Japanese(weather, _gen)}で0.5倍)");

        if (hasAte) Add(5325, $"{atkName}(1.3倍)");
        else if (atkAbility == "mega-launcher" && flags.Contains("Pulse") || atkAbility == "strong-jaw" && flags.Contains("Bite"))
            Add(6144, $"{atkName}(1.5倍)");
        else if (atkAbility == "tough-claws" && flags.Contains("Contact")) Add(5325, $"{atkName}(1.3倍)");

        if (attackerGrounded && (terrain == Terrain.Electric && moveType == "Electric" || terrain == Terrain.Grassy && moveType == "Grass"))
            Add(6144, $"{FieldNames.Japanese(terrain)}(1.5倍)");
        if (defenderGrounded && (terrain == Terrain.Misty && moveType == "Dragon" || terrain == Terrain.Grassy && move.Id is MoveIdEarthquake or MoveIdBulldoze))
            Add(2048, $"{FieldNames.Japanese(terrain)}(0.5倍)");
        return mods;
    }

    private List<int> BasePowerModsGen7(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, Move move, string moveType,
        bool isZ, IReadOnlySet<string> flags, bool hasAte, string atkAbility, string defAbility, Item? attackerItem, Item? defenderItem,
        int basePower, Terrain terrain, Weather weather, bool attackerGrounded, bool defenderGrounded, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";
        var moveId = isZ ? 0 : move.Id;

        if (moveId == MoveIdFacade && attacker.Status is StatusCondition.Burn or StatusCondition.Paralysis or StatusCondition.Poison or StatusCondition.BadlyPoisoned)
            Add(8192, "からげんき(状態異常で2倍)");
        else if (moveId == MoveEffects.MoveIdBrine && defender.CurrentHp * 2 <= defender.MaxHp)
            Add(8192, "しおみず(相手のHPが半分以下で2倍)");
        else if (moveId == MoveEffects.MoveIdVenoshock && defender.Status is StatusCondition.Poison or StatusCondition.BadlyPoisoned)
            Add(8192, "ベノムショック(相手がどくで2倍)");
        else if (moveId == MoveIdKnockOff && MoveEffects.KnockOffBoosted(defenderItem, defender)) Add(6144, "はたきおとす(持ち物があるので1.5倍)");
        else if (moveId is MoveIdSolarBeam or MoveIdSolarBlade && weather is Weather.Rain or Weather.Sand or Weather.Snow)
            Add(2048, $"{move.Name}({FieldNames.Japanese(weather, _gen)}で0.5倍)");

        if (attackerGrounded && (terrain == Terrain.Electric && moveType == "Electric" || terrain == Terrain.Grassy && moveType == "Grass" || terrain == Terrain.Psychic && moveType == "Psychic"))
            Add(6144, $"{FieldNames.Japanese(terrain)}(1.5倍)");
        if (defenderGrounded && (terrain == Terrain.Misty && moveType == "Dragon" || terrain == Terrain.Grassy && moveId is MoveIdEarthquake or MoveIdBulldoze))
            Add(2048, $"{FieldNames.Japanese(terrain)}(0.5倍)");

        if (atkAbility == "technician" && basePower <= 60 || atkAbility == "mega-launcher" && flags.Contains("Pulse") || atkAbility == "strong-jaw" && flags.Contains("Bite"))
            Add(6144, $"{atkName}(1.5倍)");

        if (atkAbility == "sheer-force" && !isZ && MoveEffects.SheerForceBoosts(move, _gen) || atkAbility == "sand-force" && weather == Weather.Sand && moveType is "Rock" or "Ground" or "Steel"
            || atkAbility == "tough-claws" && flags.Contains("Contact"))
            Add(5325, $"{atkName}(1.3倍)");

        if (hasAte) Add(4915, $"{atkName}(1.2倍)");
        // Z 技は技が置き換わるので、すてみの対象にならない
        if (atkAbility == "iron-fist" && flags.Contains("Punch") || atkAbility == "reckless" && !isZ && MoveEffects.IsRecoil(move)) Add(4915, $"{atkName}(1.2倍)");

        if (defAbility == "heatproof" && moveType == "Fire") Add(2048, $"{defName}(0.5倍)");
        else if (defAbility == "dry-skin" && moveType == "Fire") Add(5120, $"{defName}(1.25倍)");

        if (IsTypeBoost(attackerItem, moveType)) Add(4915, $"{attackerItem!.Name}(1.2倍)");
        else if (IsMuscleBand(attackerItem, move.Category == MoveCategory.Physical)) Add(4505, $"{attackerItem!.Name}(1.1倍)");
        return mods;
    }

    private List<int> AttackModsGen56(PokemonBuild attacker, Move move, string moveType, bool isPhysical, string atkAbility, string defAbility,
        Item? attackerItem, Weather weather, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var statName = isPhysical ? "A" : "C";

        if (defAbility == "thick-fat" && moveType is "Fire" or "Ice") Add(2048, $"あついしぼう({statName}0.5倍)");

        if (atkAbility == "guts" && attacker.Status != StatusCondition.None && isPhysical || Pinch(attacker, atkAbility, moveType))
            Add(6144, $"{atkName}({statName}1.5倍)");
        else if (atkAbility == "toxic-boost" && isPhysical && attacker.Status is StatusCondition.Poison or StatusCondition.BadlyPoisoned
                 || atkAbility == "flare-boost" && !isPhysical && attacker.Status == StatusCondition.Burn)
            Add(6144, $"{atkName}({StatusNames.Japanese(attacker.Status)}で{statName}1.5倍)");
        else if (atkAbility == "solar-power" && weather == Weather.Sun && !isPhysical) Add(6144, $"{atkName}({statName}1.5倍)");
        else if (atkAbility == "defeatist" && attacker.CurrentHp * 2 <= attacker.MaxHp) Add(2048, $"{atkName}({statName}0.5倍)");
        else if (atkAbility is "huge-power" or "pure-power" && isPhysical) Add(8192, $"{atkName}({statName}2倍)");

        if (attackerItem is { Category: ItemCategory.ThickClub } && isPhysical && attacker.Pokemon.SpeciesId is 104 or 105
            || attackerItem is { Category: ItemCategory.LightBall } && attacker.Pokemon.SpeciesId == 25)
            Add(8192, $"{attackerItem.Name}({statName}2倍)");
        else if (IsChoiceBand(attackerItem) && isPhysical || IsChoiceSpecs(attackerItem) && !isPhysical)
            Add(6144, $"{attackerItem!.Name}({statName}1.5倍)");
        return mods;
    }

    private List<int> AttackModsGen7(PokemonBuild attacker, Move move, string moveType, bool isPhysical, bool isZ, string atkAbility, string defAbility,
        Item? attackerItem, Weather weather, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var statName = isPhysical ? "A" : "C";

        if (atkAbility == "defeatist" && attacker.CurrentHp * 2 <= attacker.MaxHp) Add(2048, $"{atkName}({statName}0.5倍)");
        else if (atkAbility == "solar-power" && weather == Weather.Sun && !isPhysical) Add(6144, $"{atkName}({statName}1.5倍)");
        else if (atkAbility == "guts" && attacker.Status != StatusCondition.None && isPhysical || Pinch(attacker, atkAbility, moveType))
            Add(6144, $"{atkName}({statName}1.5倍)");
        else if (atkAbility == "toxic-boost" && isPhysical && attacker.Status is StatusCondition.Poison or StatusCondition.BadlyPoisoned
                 || atkAbility == "flare-boost" && !isPhysical && attacker.Status == StatusCondition.Burn)
            Add(6144, $"{atkName}({StatusNames.Japanese(attacker.Status)}で{statName}1.5倍)");
        else if (atkAbility == "steelworker" && moveType == "Steel") Add(6144, $"{atkName}({statName}1.5倍)");
        else if (atkAbility == "water-bubble" && moveType == "Water" || atkAbility is "huge-power" or "pure-power" && isPhysical)
            Add(8192, $"{atkName}({statName}2倍)");

        if (defAbility == "thick-fat" && moveType is "Fire" or "Ice" || defAbility == "water-bubble" && moveType == "Fire")
            Add(2048, $"{(defAbility == "thick-fat" ? "あついしぼう" : "すいほう")}({statName}0.5倍)");

        if (attackerItem is { Category: ItemCategory.ThickClub } && isPhysical && attacker.Pokemon.SpeciesId is 104 or 105
            || attackerItem is { Category: ItemCategory.LightBall } && attacker.Pokemon.SpeciesId == 25 && !isZ)
            Add(8192, $"{attackerItem.Name}({statName}2倍)");
        else if (!isZ && (IsChoiceBand(attackerItem) && isPhysical || IsChoiceSpecs(attackerItem) && !isPhysical))
            Add(6144, $"{attackerItem!.Name}({statName}1.5倍)");
        return mods;
    }

    private static bool Pinch(PokemonBuild attacker, string ability, string moveType) =>
        attacker.CurrentHp * 3 <= attacker.MaxHp && (ability == "overgrow" && moveType == "Grass" || ability == "blaze" && moveType == "Fire"
            || ability == "torrent" && moveType == "Water" || ability == "swarm" && moveType == "Bug");

    private static List<int> DefenseModsGen56(PokemonBuild defender, bool hitsPhysical, string defAbility, Item? defenderItem, Terrain terrain, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var defName = defender.Ability?.Name ?? "";
        var statName = hitsPhysical ? "B" : "D";

        if (defAbility == "marvel-scale" && defender.Status != StatusCondition.None && hitsPhysical) Add(6144, $"{defName}({statName}1.5倍)");
        if (defAbility == "grass-pelt" && terrain == Terrain.Grassy && hitsPhysical) Add(6144, $"{defName}({statName}1.5倍)");
        if (defenderItem is { Category: ItemCategory.Eviolite } && defender.Pokemon.NotFullyEvolved || IsAssaultVest(defenderItem) && !hitsPhysical)
            Add(6144, $"{defenderItem!.Name}({statName}1.5倍)");
        if (defAbility == "fur-coat" && hitsPhysical) Add(8192, $"{defName}({statName}2倍)");
        return mods;
    }

    private static List<int> DefenseModsGen7(PokemonBuild defender, bool hitsPhysical, string defAbility, Item? defenderItem, Terrain terrain, List<string> modifiers)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var defName = defender.Ability?.Name ?? "";
        var statName = hitsPhysical ? "B" : "D";

        if (defAbility == "marvel-scale" && defender.Status != StatusCondition.None && hitsPhysical) Add(6144, $"{defName}({statName}1.5倍)");
        else if (defAbility == "grass-pelt" && terrain == Terrain.Grassy && hitsPhysical) Add(6144, $"{defName}({statName}1.5倍)");
        else if (defAbility == "fur-coat" && hitsPhysical) Add(8192, $"{defName}({statName}2倍)");
        if (defenderItem is { Category: ItemCategory.Eviolite } && defender.Pokemon.NotFullyEvolved || IsAssaultVest(defenderItem) && !hitsPhysical)
            Add(6144, $"{defenderItem!.Name}({statName}1.5倍)");
        return mods;
    }

    private List<int> FinalMods(DamageRequest request, PokemonBuild attacker, PokemonBuild defender, Move move, string moveType, bool isPhysical,
        bool isZ, IReadOnlySet<string> flags, double effectiveness, bool isCritical, bool reflect, bool lightScreen,
        string atkAbility, string defAbility, Item? attackerItem, Item? defenderItem, List<string> modifiers, bool firstHit)
    {
        var mods = new List<int>();
        void Add(int value, string text) { mods.Add(value); modifiers.Add(text); }
        var atkName = attacker.Ability?.Name ?? "";
        var defName = defender.Ability?.Name ?? "";
        var doubles = request.Format == BattleFormat.Doubles;

        // 壁は技の分類で決まる（サイコショックは特殊技なのでひかりのかべ）
        if (!isCritical && (reflect && isPhysical || lightScreen && !isPhysical))
        {
            var wall = isPhysical ? "リフレクター" : "ひかりのかべ";
            if (doubles) Add(_gen > 5 ? 2732 : 2703, $"{wall}(ダブル 0.67倍)");
            else Add(2048, $"{wall}(0.5倍)");
        }

        var fullHp = defender.IsFullHp;
        if (_gen >= 7)
        {
            if (atkAbility == "neuroforce" && effectiveness > 1) Add(5120, $"{atkName}(1.25倍)");
            else if (atkAbility == "sniper" && isCritical) Add(6144, $"{atkName}(急所1.5倍)");
            else if (atkAbility == "tinted-lens" && effectiveness < 1) Add(8192, $"{atkName}(2倍)");
            if (defAbility is "multiscale" or "shadow-shield" && fullHp && firstHit) Add(2048, $"{defName}(HP満タン 0.5倍)");
            if (defAbility == "fluffy" && flags.Contains("Contact")) Add(2048, $"{defName}(接触技0.5倍)");
            if (defAbility is "solid-rock" or "filter" or "prism-armor" && effectiveness > 1) Add(3072, $"{defName}(0.75倍)");
            if (defAbility == "fluffy" && moveType == "Fire") Add(8192, $"{defName}(ほのお技2倍)");
            if (attackerItem is { Category: ItemCategory.ExpertBelt } && effectiveness > 1 && !isZ) Add(4915, $"{attackerItem.Name}(1.2倍)");
            else if (attackerItem is { Category: ItemCategory.LifeOrb }) Add(5324, $"{attackerItem.Name}(1.3倍)");
        }
        else
        {
            if (defAbility == "multiscale" && fullHp && firstHit) Add(2048, $"{defName}(HP満タン 0.5倍)");
            if (atkAbility == "tinted-lens" && effectiveness < 1) Add(8192, $"{atkName}(2倍)");
            if (atkAbility == "sniper" && isCritical) Add(6144, $"{atkName}(急所1.5倍)");
            if (defAbility is "solid-rock" or "filter" && effectiveness > 1) Add(3072, $"{defName}(0.75倍)");
            if (attackerItem is { Category: ItemCategory.ExpertBelt } && effectiveness > 1) Add(4915, $"{attackerItem.Name}(1.2倍)");
            else if (attackerItem is { Category: ItemCategory.LifeOrb }) Add(5324, $"{attackerItem.Name}(1.3倍)");
        }

        if (firstHit && defenderItem is { Category: ItemCategory.ResistBerry } berry && berry.TypeBoost == moveType && (effectiveness > 1 || moveType == "Normal"))
            Add(2048, $"{berry.Name}(1発目のみ 0.5倍)");
        return mods;
    }

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
}

/// <summary>Z 技（第 7 世代）の威力。元の技の威力から表で決まり、一部の技は個別の値。</summary>
public static class ZMove
{
    /// <summary>表と違う Z 技の威力（技 ID → 威力）。連続技・威力が変わる技など。</summary>
    private static readonly Dictionary<int, int> Exceptions = new()
    {
        [42] = 140,   // ミサイルばり
        [72] = 120,   // メガドレイン
        [131] = 120,  // とげキャノン
        [167] = 120,  // トリプルキック
        [198] = 140,  // ボーンラッシュ
        [311] = 160,  // ウェザーボール
        [331] = 140,  // タネマシンガン
        [333] = 140,  // つららばり
        [350] = 140,  // ロックブラスト
        [458] = 140,  // ダブルアタック
        [500] = 160,  // アシストパワー
        [506] = 160,  // たたりめ
        [541] = 140,  // スイープビンタ
        [544] = 180,  // ギアソーサー
        [557] = 220,  // Vジェネレート
        [560] = 170,  // フライングプレス
        [614] = 180,  // サウザンアロー
        [616] = 185,  // グランドフォース
        [681] = 160,  // パワートリップ
        [687] = 140,  // コアパニッシャー
        [718] = 185,  // マルチアタック
        [742] = 180,  // ダブルパンツァー
    };

    public static int Power(Move move)
    {
        if (Exceptions.TryGetValue(move.Id, out var power)) return power;
        return move.Power switch
        {
            >= 140 => 200,
            >= 130 => 195,
            >= 120 => 190,
            >= 110 => 185,
            >= 100 => 180,
            >= 90 => 175,
            >= 80 => 160,
            >= 70 => 140,
            >= 60 => 120,
            _ => 100,
        };
    }
}
