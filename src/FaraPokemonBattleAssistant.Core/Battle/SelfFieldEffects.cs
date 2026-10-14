using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Battle;

/// <summary>
/// 攻撃側が自分の特性で作る天候・フィールド（ひでり・あめふらし・サイコメイカー・ハドロンエンジン・デルタストリームなど）の下で、
/// 技の威力・タイプ・相性がどう変わるか。タイプ相性表の「抜群を取れる技」の並べ替えに使う（ダメージ計算機は天候・フィールドの欄で扱う）。
/// 威力に掛かる補正と、天候のダメージ補正（晴れのほのお 1.5 倍など）をまとめて「威力の倍率」として返す。
/// ひひいろのこどう（晴れで攻撃 1.33 倍）・ハドロンエンジン（エレキフィールドで特攻 1.33 倍）は能力の補正だが、ダメージへの効き方は同じなので威力の倍率に含める。
/// </summary>
public static class SelfFieldEffects
{
    private const int MoveIdSolarBeam = 76;
    private const int MoveIdWeatherBall = 311;
    private const int MoveIdSolarBlade = 669;
    private const int MoveIdExpandingForce = 797;
    private const int MoveIdMistyExplosion = 802;
    private const int MoveIdRisingVoltage = 804;
    private const int MoveIdTerrainPulse = 805;
    private const int MoveIdPsyblade = 875;
    private const int MoveIdHydroSteam = 876;
    private const int MoveIdEarthquake = 89;
    private const int MoveIdMagnitude = 222;
    private const int MoveIdBulldoze = 523;
    private const int MoveIdBodyPress = 776;

    /// <param name="Type">天候・フィールドで変わった後の技のタイプ（ウェザーボール・だいちのはどう）。</param>
    /// <param name="PowerFactor">威力（と天候のダメージ補正）に掛かる倍率。</param>
    /// <param name="Fails">技が出ない（おわりのだいちのみず技・はじまりのうみのほのお技・サイコフィールドで地面にいる相手への先制技）。</param>
    /// <param name="StrongWinds">デルタストリーム（ひこうタイプの弱点が等倍になる）。</param>
    /// <param name="Field">
    /// 天候・フィールドの名前（「サイコフィールド込み」の表示用。威力に補正が掛からなかったときは null）。
    /// ひひいろのこどう・ハドロンエンジンの能力の補正が掛かったときは特性の名前（天候・フィールドの補正もあれば「晴れ・ひひいろのこどう」）。
    /// </param>
    public sealed record Result(string Type, double PowerFactor, bool Fails, bool StrongWinds, string? Field);

    /// <summary>この特性が作る天候・フィールドの名前（作らなければ null）。</summary>
    public static string? FieldName(string? ability, int generation)
    {
        if (ability == "desolate-land") return "おわりのだいち";
        if (ability == "primordial-sea") return "はじまりのうみ";
        if (ability == "delta-stream") return "デルタストリーム";
        if (FieldEffects.WeatherFromAbility(ability) is { } w) return FieldNames.Japanese(w, generation);
        if (FieldEffects.TerrainFromAbility(ability) is { } t) return FieldNames.Japanese(t);
        return null;
    }

    /// <summary>
    /// <paramref name="attacker"/>（特性 <paramref name="attackerAbility"/>）が作る天候・フィールドで、<paramref name="move"/>（タイプは <paramref name="type"/>。スキン系で変わった後）が
    /// <paramref name="target"/> に当たるときの補正。天候・フィールドを作らない特性なら補正なし（ちからもち・ヨガパワーの物理技 2 倍だけは掛かる）。
    /// 相手がノーてんき・エアロックなら天候の効果は無い。フィールドの補正は第 7 世代 1.5 倍、第 8 世代から 1.3 倍。
    /// </summary>
    public static Result For(Move move, string type, Pokemon attacker, Ability? attackerAbility, Pokemon target, Ability? targetAbility, int generation)
    {
        var ability = attackerAbility?.Identifier;
        var name = FieldName(ability, generation);
        if (name is null)
        {
            // 天候・フィールドを作らない特性でも、攻撃を 2 倍にするちからもち・ヨガパワーは、物理技の威力の倍率に含める
            // （ボディプレスは防御で計算するので掛からない）。タイプ相性表の「威力 × 倍率」の並び順に効く
            if (ability is "huge-power" or "pure-power" && move.Category == MoveCategory.Physical && move.Id != MoveIdBodyPress)
                return new Result(type, 2.0, false, false, attackerAbility!.Name);
            return new Result(type, 1.0, false, false, null);
        }

        var weatherNegated = targetAbility?.Identifier is "cloud-nine" or "air-lock";
        var weather = weatherNegated ? null : FieldEffects.WeatherFromAbility(ability);
        var heavyRain = !weatherNegated && ability == "primordial-sea";
        var harshSun = !weatherNegated && ability == "desolate-land";
        var strongWinds = !weatherNegated && ability == "delta-stream";
        var terrain = generation >= 7 ? FieldEffects.TerrainFromAbility(ability) : null;
        var attackerGrounded = !attacker.HasType("Flying");
        var targetGrounded = !target.HasType("Flying") && targetAbility?.Identifier != "levitate";
        var factor = 1.0;
        var applied = false;
        void Mul(double x) { factor *= x; applied = true; }

        // タイプが変わる技
        if (move.Id == MoveIdWeatherBall && weather is { } wb)
        {
            type = wb switch { Weather.Sun => "Fire", Weather.Rain => "Water", Weather.Sand => "Rock", _ => "Ice" };
            Mul(2); // 威力 50 → 100
        }
        if (move.Id == MoveIdTerrainPulse && terrain is { } tp && attackerGrounded)
        {
            type = tp switch { Terrain.Electric => "Electric", Terrain.Grassy => "Grass", Terrain.Psychic => "Psychic", _ => "Fairy" };
            Mul(2); // 威力 50 → 100
        }

        // 技が出ない
        var fails = harshSun && type == "Water" || heavyRain && type == "Fire"
            || terrain == Terrain.Psychic && targetGrounded && move.Priority > 0;
        if (fails) return new Result(type, 0, true, strongWinds, name);

        // 天候（ダメージの 1.5 倍 / 0.5 倍も威力の倍率として扱う）
        if (weather == Weather.Sun)
        {
            if (type == "Fire") Mul(1.5);
            else if (move.Id == MoveIdHydroSteam) Mul(1.5);
            else if (type == "Water") Mul(0.5);
        }
        else if (weather == Weather.Rain)
        {
            if (type == "Water") Mul(1.5);
            else if (type == "Fire") Mul(0.5);
        }
        if (move.Id is MoveIdSolarBeam or MoveIdSolarBlade && weather is Weather.Rain or Weather.Sand or Weather.Snow)
            Mul(0.5);

        // フィールド
        var terrainBoost = generation >= 8 ? 1.3 : 1.5;
        switch (terrain)
        {
            case Terrain.Electric when attackerGrounded && type == "Electric":
            case Terrain.Grassy when attackerGrounded && type == "Grass":
            case Terrain.Psychic when attackerGrounded && type == "Psychic":
                Mul(terrainBoost);
                break;
            case Terrain.Misty when targetGrounded && type == "Dragon":
                Mul(0.5);
                break;
        }
        if (terrain == Terrain.Grassy && targetGrounded && move.Id is MoveIdEarthquake or MoveIdMagnitude or MoveIdBulldoze) Mul(0.5);
        if (terrain == Terrain.Psychic && attackerGrounded && move.Id == MoveIdExpandingForce) Mul(1.5);
        if (terrain == Terrain.Electric && targetGrounded && move.Id == MoveIdRisingVoltage) Mul(2);
        if (terrain == Terrain.Electric && move.Id == MoveIdPsyblade) Mul(1.5);
        if (terrain == Terrain.Misty && attackerGrounded && move.Id == MoveIdMistyExplosion) Mul(1.5);

        // ひひいろのこどう: 晴れで物理技（攻撃）1.33 倍、ハドロンエンジン: エレキフィールドで特殊技（特攻）1.33 倍（5461/4096）。
        // ボディプレスは防御で計算するので掛からない
        var statBoost = ability == "orichalcum-pulse" && weather == Weather.Sun && move.Category == MoveCategory.Physical && move.Id != MoveIdBodyPress
            || ability == "hadron-engine" && terrain == Terrain.Electric && move.Category == MoveCategory.Special;
        if (statBoost)
        {
            factor *= 5461.0 / 4096;
            var abilityName = attackerAbility!.Name;
            return new Result(type, factor, false, strongWinds, applied ? $"{name}・{abilityName}" : abilityName);
        }

        return new Result(type, factor, false, strongWinds, applied ? name : null);
    }
}
