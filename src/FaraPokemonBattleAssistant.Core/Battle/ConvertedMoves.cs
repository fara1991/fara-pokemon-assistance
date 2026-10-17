using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Battle;

/// <summary>ダイマックス技・Z 技に変わったあとの技（名前と威力）。計算に使う威力と同じ値。</summary>
/// <param name="Name">変換後の技名（例: ダイソウゲン、ライジングランドオーバー）。</param>
/// <param name="Power">変換後の威力。</param>
/// <param name="IsZMove">Z 技なら true、ダイマックス技なら false。</param>
public sealed record ConvertedMove(string Name, int Power, bool IsZMove)
{
    public string Kind => IsZMove ? "Z技" : "ダイマックス技";
}

/// <summary>ダイマックス技（第 8 世代）・Z 技（第 7 世代）への変換。</summary>
public static class ConvertedMoves
{
    private static readonly Dictionary<string, string> MaxNames = new()
    {
        ["Normal"] = "ダイアタック", ["Fire"] = "ダイバーン", ["Water"] = "ダイストリーム", ["Electric"] = "ダイサンダー",
        ["Grass"] = "ダイソウゲン", ["Ice"] = "ダイアイス", ["Fighting"] = "ダイナックル", ["Poison"] = "ダイアシッド",
        ["Ground"] = "ダイアース", ["Flying"] = "ダイジェット", ["Psychic"] = "ダイサイコ", ["Bug"] = "ダイワーム",
        ["Rock"] = "ダイロック", ["Ghost"] = "ダイホロウ", ["Dragon"] = "ダイドラグーン", ["Dark"] = "ダイアーク",
        ["Steel"] = "ダイスチル", ["Fairy"] = "ダイフェアリー",
    };

    public static string MaxMoveName(string type) => MaxNames.TryGetValue(type, out var n) ? n : "ダイマックス技";

    /// <summary>ダイマックス技。変化技は対象外（null）。</summary>
    public static ConvertedMove? Dynamax(Move move, string moveType) =>
        move.IsDamaging && move.Power > 0 ? new ConvertedMove(MaxMoveName(moveType), DamageCalculator.MaxMovePower(move), false) : null;

    /// <summary>Z 技。専用 Z 技（ポケモンと技の組み合わせ）があればそれ。</summary>
    public static ConvertedMove? Z(Move move, Pokemon attacker, string moveType) =>
        move.IsDamaging ? new ConvertedMove(ZMove.Name(move, attacker, moveType), ZMove.Power(move, attacker), true) : null;

    /// <summary>
    /// 計算の依頼が、ダイマックス技・Z 技に変わるか。変わるなら変換後の技。
    /// <paramref name="moveType"/> は実際に判定に使う技タイプ（<see cref="DamageResult.MoveType"/>）。
    /// </summary>
    public static ConvertedMove? For(DamageRequest request, string moveType, int generation)
    {
        if (request.Attacker.IsDynamax && generation >= 8) return Dynamax(request.Move, moveType);
        if (request.IsZMove && generation == 7) return Z(request.Move, request.Attacker.Pokemon, moveType);
        return null;
    }

    /// <summary>
    /// 計算の前（防御側を選ぶ前）に、技の下へ出す表示用。技タイプは、ウェザーボール・だいちのはどう・スキン系などを計算と同じ規則で決める。
    /// </summary>
    public static ConvertedMove? Preview(Move move, PokemonBuild attacker, bool zMove, int generation,
        Weather weather = Weather.None, Terrain terrain = Terrain.None)
    {
        var dynamax = attacker.IsDynamax && generation >= 8;
        var z = zMove && generation == 7;
        if (!dynamax && !z) return null;
        var type = MoveTypeFor(move, attacker, weather, terrain);
        return dynamax ? Dynamax(move, type) : Z(move, attacker.Pokemon, type);
    }

    /// <summary>天候・フィールド・スキン系特性を反映した技タイプ（計算機本体と同じ規則）。</summary>
    public static string MoveTypeFor(Move move, PokemonBuild attacker, Weather weather, Terrain terrain)
    {
        var type = move.Type;
        var noTypeChange = move.Id is MoveEffects.MoveIdWeatherBall or MoveEffects.MoveIdTerrainPulse;
        if (move.Id == MoveEffects.MoveIdWeatherBall && MoveEffects.WeatherBallType(weather) is { } w) type = w;
        else if (move.Id == MoveEffects.MoveIdTerrainPulse && MoveEffects.TerrainPulseType(terrain) is { } t) type = t;
        else if (move.Id == MoveEffects.MoveIdRagingBull) type = MoveEffects.RagingBullType(attacker.Pokemon, type);
        var ability = attacker.Ability?.Identifier ?? "";
        var skin = noTypeChange ? null : AbilityEffects.SkinType(ability);
        if (skin is not null && type == "Normal") type = skin;
        else if (ability == "normalize" && !noTypeChange) type = "Normal";
        return type;
    }
}
