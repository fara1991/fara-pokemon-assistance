using System.Text.RegularExpressions;
using FaraPokemonAssistance.Core.Battle;
using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Text;

public sealed class DamageCommandOptions
{
    /// <summary>既定のデータセット（例: "Champions"）。</summary>
    public string DataSetKey { get; set; } = DataCatalog.DefaultDataSetKey;
    public BattleFormat Format { get; set; } = BattleFormat.Singles;
    /// <summary>攻撃側の既定努力値（攻撃技に応じて A または C に振る）。</summary>
    public int DefaultAttackerOffenseEv { get; set; } = 252;
    /// <summary>防御側の既定 HP 努力値。</summary>
    public int DefaultDefenderHpEv { get; set; } = 252;
    /// <summary>防御側の既定 B/D 努力値（技の分類に応じた方）。</summary>
    public int DefaultDefenderDefenseEv { get; set; } = 0;
    /// <summary>使用率データから性格・持ち物の既定値を埋めるか。</summary>
    public bool UseUsageDefaults { get; set; } = true;
    /// <summary>出力 1 行の最大長（Twitch は 500 文字）。</summary>
    public int MaxLength { get; set; } = 480;
    /// <summary>急所で計算する（トークンの「急所」と同じ）。</summary>
    public bool IsCritical { get; set; }
    /// <summary>
    /// 名前解決後のポケモンに対して、登録済みの構成（使用チームなど）を返す。
    /// 第 2 引数は攻撃側なら true。null を返すと既定値（使用率ベース）で埋める。
    /// </summary>
    public Func<Pokemon, bool, PokemonBuild?>? BuildProvider { get; set; }
}

public sealed class DamageCommandResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public DamageRequest? Request { get; init; }
    public DamageResult? Damage { get; init; }

    public override string ToString() => Message;
}

/// <summary>
/// チャット向けテキストコマンド。<c>!dmg 攻撃側 技 防御側 [オプション...]</c> を解釈して 1 行の結果を返す。
/// <para>オプション（順不同、名前の後ろに並べる）:</para>
/// <list type="bullet">
/// <item><c>A252 C252 S4</c> … 攻撃側の努力値、<c>H252 B4 D252</c> … 防御側の努力値</item>
/// <item>性格名（いじっぱり 等）… 上昇補正が攻撃/特攻/素早さなら攻撃側、防御/特防なら防御側</item>
/// <item>持ち物名（こだわりメガネ 等）… チョッキ・しんかのきせきは防御側、それ以外は攻撃側</item>
/// <item><c>攻:</c> / <c>防:</c> を前に付けると側を明示（例: <c>防:ずぶとい 防:たべのこし</c>）</item>
/// <item><c>+1</c>〜<c>+6</c>、<c>-1</c>〜<c>-6</c> … 攻撃側の攻撃ランク（<c>防:+1</c> で防御側の防御ランク）</item>
/// <item><c>急所</c>、<c>ダブル</c> / <c>シングル</c>、<c>Lv100</c>、<c>無振り</c>（防御側の努力値を 0 に）</item>
/// <item>特性名（ちからもち 等）… そのポケモンが持てる側に付く。両方持てるなら防御的な特性は防御側</item>
/// <item><c>テラス</c>（技タイプにテラスタル）、<c>テラスほのお</c> / <c>ほのおテラス</c>、<c>防:テラスみず</c></item>
/// <item><c>ダイマ</c>（攻撃側をダイマックス）、<c>防:ダイマ</c>（防御側: HP 2 倍）</item>
/// <item><c>壁</c>（オーロラベール。物理・特殊とも半減）、<c>リフレクター</c>（物理のみ）、<c>ひかりのかべ</c>（特殊のみ）、<c>HP50%</c>（防御側の残り HP）、<c>攻:HP30%</c>（攻撃側の残り HP。もうか等）</item>
/// <item>天候: <c>晴れ</c> <c>雨</c> <c>砂</c> <c>雪</c>、フィールド: <c>エレキ</c> <c>グラス</c> <c>サイコ</c> <c>ミスト</c></item>
/// <item>状態異常: <c>やけど</c> <c>まひ</c>（既定は攻撃側）、<c>どく</c> <c>もうどく</c>（既定は防御側。確定数に定数ダメージを織り込む）</item>
/// </list>
/// </summary>
public sealed class DamageCommand
{
    private static readonly Regex EvToken = new(@"^(?:([HABCDShabcds])(\d{1,3}))+$", RegexOptions.Compiled);
    private static readonly Regex EvPair = new(@"([HABCDShabcds])(\d{1,3})", RegexOptions.Compiled);
    private static readonly Regex BoostToken = new(@"^([+-])([1-6])$", RegexOptions.Compiled);
    private static readonly Regex LevelToken = new(@"^(?:lv\.?|lv|レベル|れべる)(\d{1,3})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly char[] Separators = { ' ', '　', '\t', ',', '、' };

    private readonly DataCatalog _catalog;

    public DamageCommand(DataCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>略称込みのポケモン名解決器（他のコマンドと共有）。</summary>
    public static NameResolver<Pokemon> CreatePokemonResolver(PokemonDataSet data) =>
        new(data.Pokemon, p => p.Name, PokemonAliases(data));

    public static string Usage =>
        "使い方: !dmg 攻撃側 技 防御側 [A252 H252 性格 持ち物 特性 テラスほのお 晴れ サイコフィールド +1 急所 ダブル ...]  例: !dmg イエッサン♂ ワイドフォース メガリザードンX サイコフィールド";

    public async Task<DamageCommandResult> ExecuteAsync(string argumentText, DamageCommandOptions? options = null, CancellationToken ct = default)
    {
        options ??= new DamageCommandOptions();
        try
        {
            return await ExecuteCoreAsync(argumentText, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"計算に失敗しました: {ex.Message}");
        }
    }

    private async Task<DamageCommandResult> ExecuteCoreAsync(string argumentText, DamageCommandOptions options, CancellationToken ct)
    {
        var tokens = (argumentText ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (tokens.Count > 0 && tokens[0].StartsWith('!')) tokens.RemoveAt(0);
        if (tokens.Count > 0 && tokens[0].Equals("damage", StringComparison.OrdinalIgnoreCase)) tokens.RemoveAt(0);
        if (tokens.Count == 0 || tokens[0] is "help" or "ヘルプ" or "?")
            return Fail(Usage);
        if (tokens.Count < 3)
            return Fail($"攻撃側・技・防御側の 3 つを指定してください。{Usage}");

        // データセット名はトークンのどこにあっても拾う（例: !dmg champions ...）
        var dataSets = await _catalog.GetDataSetsAsync(ct).ConfigureAwait(false);
        var dataSetKey = await _catalog.ResolveDefaultKeyAsync(options.DataSetKey, ct).ConfigureAwait(false);
        var dataSetWords = DataSetWords(dataSets);
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (dataSetWords.TryGetValue(NameNormalizer.Normalize(tokens[i]), out var key))
            {
                dataSetKey = key;
                tokens.RemoveAt(i);
            }
        }
        if (tokens.Count < 3)
            return Fail($"攻撃側・技・防御側の 3 つを指定してください。{Usage}");

        var data = await _catalog.GetDataSetAsync(dataSetKey, ct).ConfigureAwait(false);
        var pokemonResolver = new NameResolver<Pokemon>(data.Pokemon, p => p.Name, PokemonAliases(data));
        var moveResolver = new NameResolver<Move>(data.Moves, m => m.Name);
        var itemResolver = new NameResolver<Item>(data.Items, i => i.Name);
        var natureResolver = new NameResolver<Nature>(data.Natures, n => n.Name);
        var abilityResolver = new NameResolver<Ability>(data.Abilities, a => a.Name);

        var attackerMatch = pokemonResolver.Resolve(tokens[0]);
        if (!attackerMatch.IsResolved)
            return Fail(NotFound("ポケモン", tokens[0], attackerMatch.Candidates.Select(p => p.Name)) + await ElsewhereHintAsync(tokens[0], data, dataSets, ct).ConfigureAwait(false));
        var (moveToken, moveRank) = SplitRankSuffix(tokens[1]);
        var moveMatch = moveResolver.Resolve(moveToken);
        if (!moveMatch.IsResolved) return Fail(NotFound("技", moveToken, moveMatch.Candidates.Select(m => m.Name)));
        var defenderMatch = pokemonResolver.Resolve(tokens[2]);
        if (!defenderMatch.IsResolved)
            return Fail(NotFound("ポケモン", tokens[2], defenderMatch.Candidates.Select(p => p.Name)) + await ElsewhereHintAsync(tokens[2], data, dataSets, ct).ConfigureAwait(false));

        var move = moveMatch.Value!;
        if (!move.IsDamaging)
            return Fail($"{move.Name} はダメージを与える技ではありません。");

        var evSystem = data.Info.EvSystem;
        var attackerPreset = options.BuildProvider?.Invoke(attackerMatch.Value!, true);
        var defenderPreset = options.BuildProvider?.Invoke(defenderMatch.Value!, false);
        var attacker = attackerPreset?.Clone() ?? new PokemonBuild(attackerMatch.Value!, evSystem);
        var defender = defenderPreset?.Clone() ?? new PokemonBuild(defenderMatch.Value!, evSystem);
        attacker.EvSystem = evSystem;
        defender.EvSystem = evSystem;
        var format = options.Format;
        var isCritical = options.IsCritical;
        var attackerEvSet = attackerPreset is not null;
        var defenderEvSet = defenderPreset is not null;
        var attackerNatureSet = attackerPreset is not null;
        var defenderNatureSet = defenderPreset is not null;
        var attackerItemSet = attackerPreset is not null;
        var defenderItemSet = defenderPreset is not null;
        var defenderNoEv = false;
        var attackerAbilitySet = attackerPreset?.Ability is not null;
        var defenderAbilitySet = defenderPreset?.Ability is not null;
        if (moveRank != 0) attacker.Boosts[move.AttackStatUsed] = moveRank;
        var weather = Weather.None;
        var terrain = Terrain.None;
        var screen = false;
        var reflect = false;
        var lightScreen = false;
        var attackerTeraToMoveType = false;
        var warnings = new List<string>();

        var isPhysical = move.Category == MoveCategory.Physical;
        var offenseStat = move.AttackStatUsed;
        var defenseStat = move.DefenseStatUsed;

        foreach (var rawToken in tokens.Skip(3))
        {
            var token = rawToken;
            bool? forcedAttacker = null;
            foreach (var (prefix, side) in SidePrefixes)
            {
                if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    forcedAttacker = side;
                    token = token[prefix.Length..];
                    break;
                }
            }
            if (token.Length == 0) continue;

            var normalized = NameNormalizer.Normalize(token);

            if (normalized is "急所" or "きゅうしょ" or "crit")
            {
                isCritical = true;
                continue;
            }
            if (normalized is "りふれくたー" or "reflect")
            {
                reflect = true;
                continue;
            }
            if (normalized is "ひかりのかべ" or "光の壁" or "lightscreen")
            {
                lightScreen = true;
                continue;
            }
            if (normalized is "壁" or "かべ" or "おーろらべーる" or "screen")
            {
                screen = true;
                continue;
            }
            var hpMatch = HpToken.Match(normalized);
            if (hpMatch.Success)
            {
                var pct = Math.Clamp(int.Parse(hpMatch.Groups[1].Value), 1, 100);
                if (forcedAttacker == true) attacker.HpPercent = pct; else defender.HpPercent = pct;
                continue;
            }
            if (StatusNames.Parse(normalized) is { } status)
            {
                // 既定: やけど・まひは攻撃側（自分の火力/素早さ）、どく・もうどくは防御側（定数ダメージで確定数が変わる）
                var toAttacker = forcedAttacker ?? status is StatusCondition.Burn or StatusCondition.Paralysis;
                if (toAttacker) attacker.Status = status; else defender.Status = status;
                continue;
            }
            if (TryParseWeather(normalized) is { } w) { weather = w; continue; }
            if (TryParseTerrain(normalized) is { } t) { terrain = t; continue; }
            if (TryParseTera(normalized, out var teraType))
            {
                if (teraType is null)
                {
                    if (forcedAttacker == false) warnings.Add("防御側のテラスタイプを指定してください（例: 防:テラスみず）");
                    else attackerTeraToMoveType = true;
                }
                else if (forcedAttacker == false) defender.TeraType = teraType;
                else attacker.TeraType = teraType;
                continue;
            }
            if (normalized is "持ち物なし" or "もちものなし" or "道具なし" or "どうぐなし" or "noitem")
            {
                if (forcedAttacker == false) { defender.Item = null; defenderItemSet = true; }
                else if (forcedAttacker == true) { attacker.Item = null; attackerItemSet = true; }
                else { attacker.Item = null; defender.Item = null; attackerItemSet = defenderItemSet = true; }
                continue;
            }
            if (normalized is "ダイマ" or "だいま" or "ダイマックス" or "だいまっくす" or "dmax" or "dynamax")
            {
                if (forcedAttacker == false) defender.IsDynamax = true; else attacker.IsDynamax = true;
                continue;
            }
            if (normalized is "ダブル" or "だぶる" or "double" or "doubles" or "dbl") { format = BattleFormat.Doubles; continue; }
            if (normalized is "シングル" or "しんぐる" or "single" or "singles") { format = BattleFormat.Singles; continue; }
            if (normalized is "無振り" or "むふり" or "むぶり" or "noev")
            {
                if (forcedAttacker == true) { attacker.EVs = new StatSet(); attackerEvSet = true; }
                else { defenderNoEv = true; defenderEvSet = true; }
                continue;
            }

            var level = LevelToken.Match(token);
            if (level.Success)
            {
                var lv = Math.Clamp(int.Parse(level.Groups[1].Value), 1, 100);
                if (forcedAttacker != false) attacker.Level = lv;
                if (forcedAttacker != true) defender.Level = lv;
                continue;
            }

            var boost = BoostToken.Match(token);
            if (boost.Success)
            {
                var stage = int.Parse(boost.Groups[2].Value) * (boost.Groups[1].Value == "-" ? -1 : 1);
                if (forcedAttacker == false) defender.Boosts[defenseStat] = stage;
                else attacker.Boosts[offenseStat] = stage;
                continue;
            }

            if (EvToken.IsMatch(token))
            {
                foreach (Match pair in EvPair.Matches(token))
                {
                    var stat = StatNames.Parse(pair.Groups[1].Value.ToUpperInvariant())!.Value;
                    var value = Math.Clamp(int.Parse(pair.Groups[2].Value), 0, EvRules.MaxPerStat(evSystem));
                    var toAttacker = forcedAttacker ?? stat is Stat.Attack or Stat.SpAttack or Stat.Speed;
                    if (toAttacker)
                    {
                        if (!attackerEvSet) { attacker.EVs = new StatSet(); attackerEvSet = true; }
                        attacker.EVs[stat] = value;
                    }
                    else
                    {
                        if (!defenderEvSet) { defender.EVs = new StatSet(); defenderEvSet = true; }
                        defender.EVs[stat] = value;
                    }
                }
                continue;
            }

            var nature = natureResolver.Resolve(token);
            if (nature.IsResolved)
            {
                var n = nature.Value!;
                var toAttacker = forcedAttacker ?? n.IncreasedStat is Stat.Attack or Stat.SpAttack or Stat.Speed or null;
                if (toAttacker) { attacker.Nature = n; attackerNatureSet = true; }
                else { defender.Nature = n; defenderNatureSet = true; }
                continue;
            }

            var item = itemResolver.Resolve(token);
            if (item.IsResolved)
            {
                var it = item.Value!;
                var toAttacker = forcedAttacker ?? !it.IsDefensive;
                if (toAttacker) { attacker.Item = it; attackerItemSet = true; }
                else { defender.Item = it; defenderItemSet = true; }
                continue;
            }

            var ability = abilityResolver.Resolve(token);
            if (ability.IsResolved)
            {
                var ab = ability.Value!;
                var attackerCan = attacker.Pokemon.AbilityIds.Contains(ab.Id);
                var defenderCan = defender.Pokemon.AbilityIds.Contains(ab.Id);
                var toAttacker = forcedAttacker ?? (attackerCan && !defenderCan ? true
                    : defenderCan && !attackerCan ? false
                    : !AbilityEffects.IsDefensive(ab.Identifier));
                if (toAttacker) { attacker.Ability = ab; attackerAbilitySet = true; }
                else { defender.Ability = ab; defenderAbilitySet = true; }
                continue;
            }

            warnings.Add($"「{rawToken}」は無視");
        }

        // --- 既定値
        UsageData? usage = null;
        if (options.UseUsageDefaults)
        {
            usage = await data.GetUsageAsync(format, ct).ConfigureAwait(false);
            if (usage.IsEmpty) usage = null;
        }

        if (!attackerEvSet)
            attacker.EVs[offenseStat] = Math.Min(options.DefaultAttackerOffenseEv, EvRules.Full(evSystem));
        if (!defenderEvSet)
        {
            defender.EVs[Stat.HP] = Math.Min(options.DefaultDefenderHpEv, EvRules.Full(evSystem));
            defender.EVs[defenseStat] = Math.Min(options.DefaultDefenderDefenseEv, EvRules.Full(evSystem));
        }
        else if (defenderNoEv)
        {
            defender.EVs = new StatSet();
        }

        if (!attackerNatureSet)
            attacker.Nature = usage?.TopNature(attacker.Pokemon.Id) ?? data.FindNature("まじめ") ?? data.Natures.FirstOrDefault(n => n.IsNeutral);
        if (!defenderNatureSet)
            defender.Nature = usage?.TopNature(defender.Pokemon.Id) ?? data.FindNature("まじめ") ?? data.Natures.FirstOrDefault(n => n.IsNeutral);
        if (!attackerItemSet && usage is not null)
            attacker.Item = usage.TopItem(attacker.Pokemon.Id);
        if (!defenderItemSet && usage is not null)
            defender.Item = usage.TopItem(defender.Pokemon.Id);
        if (!attackerAbilitySet)
            attacker.Ability = usage?.DefaultAbility(attacker.Pokemon) ?? AbilityEffects.ChooseDefault(data.AbilitiesOf(attacker.Pokemon), null);
        if (!defenderAbilitySet)
            defender.Ability = usage?.DefaultAbility(defender.Pokemon) ?? AbilityEffects.ChooseDefault(data.AbilitiesOf(defender.Pokemon), null);
        if (attackerTeraToMoveType)
            attacker.TeraType = AbilityEffects.SkinType(attacker.Ability?.Identifier ?? "") is { } skin && move.Type == "Normal" ? skin : move.Type;

        if (!data.CanLearn(attacker.Pokemon.Id, move.Id))
            warnings.Add($"{attacker.Pokemon.Name}は{move.Name}を覚えません");
        foreach (var b in new[] { attacker, defender })
        {
            if (b.Pokemon.IsProvisional)
                warnings.Add($"{b.Pokemon.Name}は{data.Info.Name}未収録のため第9世代データで計算");
        }

        var request = new DamageRequest
        {
            Attacker = attacker,
            Move = move,
            Defender = defender,
            Format = format,
            IsCritical = isCritical,
            Weather = weather,
            Terrain = terrain,
            Screen = screen,
            Reflect = reflect,
            LightScreen = lightScreen,
        };
        var result = new DamageCalculator(data.TypeChart).Calculate(request);

        var message = Format(request, result, data.Info, warnings);
        if (message.Length > options.MaxLength)
            message = message[..(options.MaxLength - 1)] + "…";

        return new DamageCommandResult { Success = true, Message = message, Request = request, Damage = result };
    }

    public static string Format(DamageRequest request, DamageResult result, DataSetInfo dataSet, IReadOnlyList<string>? warnings = null)
    {
        var a = request.Attacker;
        var d = request.Defender;
        var tags = new List<string>();
        if (result.TypeEffectiveness != 1.0) tags.Add(result.EffectivenessText);
        if (request.IsCritical) tags.Add("急所");
        if (request.Weather != Weather.None) tags.Add(FieldNames.Japanese(request.Weather));
        if (request.Terrain != Terrain.None) tags.Add(FieldNames.Japanese(request.Terrain));
        if (request.Screen) tags.Add("壁");
        else
        {
            if (request.Reflect) tags.Add("リフレクター");
            if (request.LightScreen) tags.Add("ひかりのかべ");
        }
        if (request.Format == BattleFormat.Doubles) tags.Add("ダブル");
        var tagText = tags.Count > 0 ? $" [{string.Join(" ", tags)}]" : "";
        var warnText = warnings is { Count: > 0 } ? $" ※{string.Join("、", warnings)}" : "";

        return $"{a.Pokemon.Name}({a.DescribeShort()}) {request.Move.Name} → {d.Pokemon.Name}({d.DescribeShort()} HP{result.DefenderHP}): " +
               $"{result.RangeText} {result.KnockOut}{tagText}{warnText}";
    }

    /// <summary>「HP50%」「残りHP30%」（正規化後の小文字）。</summary>
    private static readonly Regex HpToken = new(@"^(?:残り)?hp(\d{1,3})[%％]$", RegexOptions.Compiled);

    private static readonly Regex RankSuffix = new(@"^(.*?)([+-＋－][1-6])$", RegexOptions.Compiled);

    /// <summary>「じしん+2」→ ("じしん", 2)。</summary>
    public static (string Name, int Rank) SplitRankSuffix(string token)
    {
        var m = RankSuffix.Match(token);
        if (!m.Success || m.Groups[1].Value.Length == 0) return (token, 0);
        var sign = m.Groups[2].Value[0] is '-' or '－' ? -1 : 1;
        return (m.Groups[1].Value, sign * (m.Groups[2].Value[1] - '0'));
    }

    /// <summary>「晴れ」「雨」などの天候トークンを解釈する（正規化前の文字列でよい）。</summary>
    public static Weather? ParseWeather(string token) => TryParseWeather(NameNormalizer.Normalize(token));
    /// <summary>「サイコ」「エレキフィールド」などのフィールドトークンを解釈する。</summary>
    public static Terrain? ParseTerrain(string token) => TryParseTerrain(NameNormalizer.Normalize(token));

    private static Weather? TryParseWeather(string n) => n switch
    {
        "晴れ" or "はれ" or "にほんばれ" or "日本晴れ" or "sun" or "sunny" => Weather.Sun,
        "雨" or "あめ" or "あまごい" or "雨乞い" or "rain" => Weather.Rain,
        "砂" or "すな" or "すなあらし" or "砂嵐" or "sand" => Weather.Sand,
        "雪" or "ゆき" or "ゆきげしき" or "あられ" or "snow" or "hail" => Weather.Snow,
        _ => null,
    };

    private static Terrain? TryParseTerrain(string n) => n switch
    {
        "えれき" or "えれきふぃーるど" or "ef" or "electric" => Terrain.Electric,
        "ぐらす" or "ぐらすふぃーるど" or "gf" or "grassy" => Terrain.Grassy,
        "さいこ" or "さいこふぃーるど" or "pf" or "psychic" => Terrain.Psychic,
        "みすと" or "みすとふぃーるど" or "mf" or "misty" => Terrain.Misty,
        _ => null,
    };

    /// <summary>「テラス」「テラスほのお」「ほのおテラス」「tera:fire」を解釈する。テラスだけなら type は null。</summary>
    private static bool TryParseTera(string n, out string? type)
    {
        type = null;
        const string word = "てらす";
        string? rest = null;
        if (n.StartsWith(word)) rest = n[word.Length..];
        else if (n.EndsWith(word)) rest = n[..^word.Length];
        else if (n.StartsWith("tera")) rest = n[4..];
        else return false;
        rest = rest.TrimStart(':', '：', 'た', 'る');
        if (rest.Length == 0) return true;
        foreach (var t in TypeNames.All)
        {
            if (NameNormalizer.Normalize(TypeNames.ToJapanese(t)) == rest || t.Equals(rest, StringComparison.OrdinalIgnoreCase))
            {
                type = t;
                return true;
            }
        }
        return false;
    }

    private static readonly (string Prefix, bool IsAttacker)[] SidePrefixes =
    {
        ("攻:", true), ("攻：", true), ("a:", true), ("atk:", true), ("こうげき:", true),
        ("防:", false), ("防：", false), ("d:", false), ("def:", false), ("ぼうぎょ:", false),
    };

    private static Dictionary<string, string> DataSetWords(IEnumerable<DataSetInfo> dataSets)
    {
        var words = new Dictionary<string, string>();
        foreach (var ds in dataSets)
        {
            words.TryAdd(NameNormalizer.Normalize(ds.Key), ds.Key);
            words.TryAdd(NameNormalizer.Normalize(ds.Name), ds.Key);
            var extra = ds.Key switch
            {
                "Champions" => new[] { "チャンピオンズ", "champion", "pc" },
                "Gen9" => new[] { "SV", "スカーレット", "バイオレット", "第9世代" },
                "Gen8" => new[] { "剣盾", "ソード", "シールド", "第8世代" },
                _ => Array.Empty<string>(),
            };
            foreach (var w in extra)
                words.TryAdd(NameNormalizer.Normalize(w), ds.Key);
        }
        return words;
    }

    /// <summary>よく使う略称。メガ○○ → 「メガ」+ 略称 もここで吸収する。</summary>
    internal static IEnumerable<(string Alias, string Name)> PokemonAliases(PokemonDataSet data)
    {
        var byName = new HashSet<string>(data.Pokemon.Select(p => p.Name));
        var aliases = new (string Alias, string Name)[]
        {
            ("メガリザX", "メガリザードンX"), ("メガリザY", "メガリザードンY"), ("リザX", "メガリザードンX"), ("リザY", "メガリザードンY"),
            ("メガガルド", "メガガルーラ"), ("メガガブ", "メガガブリアス"), ("メガバシャ", "メガバシャーモ"), ("メガボーマンダ", "メガボーマンダ"),
            ("メガマンダ", "メガボーマンダ"), ("メガクチート", "メガクチート"), ("メガゲンガー", "メガゲンガー"), ("メガルカ", "メガルカリオ"),
            ("メガバンギ", "メガバンギラス"), ("メガメタグロス", "メガメタグロス"), ("メガグロス", "メガメタグロス"), ("メガミミロップ", "メガミミロップ"),
            ("ガブ", "ガブリアス"), ("バシャ", "バシャーモ"), ("マンダ", "ボーマンダ"), ("バンギ", "バンギラス"), ("グロス", "メタグロス"),
            ("ハバタクカミ", "ハバタクカミ"), ("カミ", "ハバタクカミ"), ("テツノツツミ", "テツノツツミ"), ("ツツミ", "テツノツツミ"),
            ("イエッサン♂", "イエッサン(♂)"), ("イエッサンオス", "イエッサン(♂)"), ("イエッサンメス", "イエッサン(♀)"), ("イエッサン♀", "イエッサン(♀)"),
            ("ニャオニクス♂", "ニャオニクス(♂)"), ("ニャオニクス♀", "ニャオニクス(♀)"),
            ("ランドロス", "ランドロス(れいじゅうフォルム)"), ("霊獣ランド", "ランドロス(れいじゅうフォルム)"), ("化身ランド", "ランドロス"),
            ("ウーラオス", "ウーラオス"), ("連撃ウーラオス", "ウーラオス(れんげきのかた)"), ("水ウーラ", "ウーラオス(れんげきのかた)"), ("悪ウーラ", "ウーラオス"),
            ("ガオガエン", "ガオガエン"), ("ドラパルト", "ドラパルト"), ("パオジアン", "パオジアン"), ("パオ", "パオジアン"),
        };
        foreach (var (alias, name) in aliases)
        {
            if (byName.Contains(name)) yield return (alias, name);
        }
    }

    /// <summary>別のデータセットにその名前があれば、切り替え方を案内する。</summary>
    private async Task<string> ElsewhereHintAsync(string query, PokemonDataSet current, IReadOnlyList<DataSetInfo> dataSets, CancellationToken ct)
    {
        foreach (var info in dataSets.Reverse())
        {
            if (info.Key == current.Info.Key) continue;
            PokemonDataSet other;
            try { other = await _catalog.GetDataSetAsync(info.Key, ct).ConfigureAwait(false); }
            catch { continue; }
            var match = new NameResolver<Pokemon>(other.Pokemon, p => p.Name, PokemonAliases(other)).Resolve(query);
            if (match.IsResolved)
                return $"（{match.Value!.Name}は「{info.Name}」のデータにあります。現在は「{current.Info.Name}」です。コマンドに {info.Key.ToLowerInvariant()} を足すか、データを切り替えてください）";
        }
        return "";
    }

    private static string NotFound(string kind, string query, IEnumerable<string> candidates)
    {
        var list = candidates.Take(5).ToList();
        return list.Count > 0
            ? $"{kind}「{query}」が特定できません。候補: {string.Join("、", list)}"
            : $"{kind}「{query}」が見つかりません。";
    }

    private static DamageCommandResult Fail(string message) => new() { Success = false, Message = message };
}
