using System.Text.RegularExpressions;
using FaraPokemonAssistance.Core.Battle;
using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;
using FaraPokemonAssistance.Core.Roster;

namespace FaraPokemonAssistance.Core.Text;

public sealed class PokeCommandOptions
{
    public BattleFormat Format { get; set; } = BattleFormat.Singles;
    public int MaxLength { get; set; } = 480;
    /// <summary>登録・削除などの変更系コマンドを許可するか（ボット側で配信者本人とモデレーターだけ true にする想定）。</summary>
    public bool AllowMutations { get; set; } = true;
}

/// <summary>
/// <c>!pokech</c> / <c>!pokesv</c> / <c>!pokess</c> で始まるコマンド群。接頭辞でデータセットが決まる。
/// <list type="bullet">
/// <item><c>add 名前 H A B C D S 性格 [特性]</c> 登録、<c>ls [名前]</c> 一覧、<c>more ID</c> 詳細、<c>rm ID</c> 削除</item>
/// <item><c>add team 番号 ID [持ち物]</c>、<c>rm team 番号 [名前]</c>、<c>use team 番号</c>、<c>ls team</c>、<c>more team 番号</c></item>
/// <item><c>dmg 攻撃側 技±ランク 防御側 [オプション]</c> 通常と急所、<c>calc 攻撃側 技±ランク 防御側 受けたダメージ[%]</c> 努力値推定、<c>diff 自分 相手</c> 素早さ比較</item>
/// </list>
/// 使用チームに入っているポケモンは、その努力値・性格・特性・持ち物で計算される。
/// </summary>
public sealed class PokeCommand
{
    private static readonly char[] Separators = { ' ', '　', '\t', ',', '、' };
    private static readonly Regex PercentToken = new(@"^(\d+(?:\.\d+)?)[%％]$", RegexOptions.Compiled);

    private readonly DataCatalog _catalog;
    private readonly RosterRepository _roster;
    private readonly DamageCommand _damage;

    public PokeCommand(DataCatalog catalog, RosterRepository roster)
    {
        _catalog = catalog;
        _roster = roster;
        _damage = new DamageCommand(catalog);
    }

    public static string Usage(string prefix) =>
        $"!{prefix} add 名前 H A B C D S 性格 | ls [名前] | more ID | rm ID | add team 番号 ID [持ち物] | rm team 番号 [名前] | use team 番号 | unuse team | ls team | more team 番号 | dmg 攻撃 技±ランク 防御 | calc 攻撃 技 防御 ダメージ | diff 自分 相手";

    /// <summary>メッセージがこのコマンド群のものかを判定し、接頭辞と残りを返す。</summary>
    public async Task<(DataSetInfo DataSet, string Arguments)?> MatchAsync(string message, CancellationToken ct = default)
    {
        var text = (message ?? "").Trim();
        if (text.StartsWith('!')) text = text[1..];
        var space = text.IndexOfAny(Separators);
        var head = space < 0 ? text : text[..space];
        var rest = space < 0 ? "" : text[(space + 1)..];
        var sets = await _catalog.GetDataSetsAsync(ct).ConfigureAwait(false);
        var ds = sets.FirstOrDefault(d => d.CommandPrefix.Equals(head, StringComparison.OrdinalIgnoreCase));
        if (ds is null && head.Equals(GenericPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // !poke … は既定データセット（チャンピオンズ）として扱う
            var key = await _catalog.ResolveDefaultKeyAsync(null, ct).ConfigureAwait(false);
            ds = sets.FirstOrDefault(d => d.Key == key);
        }
        return ds is null ? null : (ds, rest);
    }

    /// <summary>データセットを選ばない共通接頭辞（<c>!poke cmd ls</c> など）。</summary>
    public const string GenericPrefix = "poke";

    /// <summary>使えるコマンドの一覧（<c>cmd ls</c>）。</summary>
    public static string CommandList(IEnumerable<DataSetInfo> dataSets)
    {
        var prefixes = string.Join(" ", dataSets.Select(d => $"!{d.CommandPrefix}={d.Name.Split('（')[0]}"));
        return "登録: add 名前 H A B C D S 性格 [特性] / ls [名前] / more ID / rm ID ｜ " +
               "チーム: add team 番号 ID [持ち物] / rm team 番号 [名前] / use team 番号 / unuse team / ls team / more team 番号 ｜ " +
               "計算: dmg 攻撃 技±ランク 防御 [努力値 性格 持ち物 特性 急所 やけど どく まひ 天候 …] / calc 攻撃 技 防御 ダメージ[%] / diff 自分 相手 [まひ] ｜ " +
               $"接頭辞: {prefixes}（!poke はチャンピオンズ）";
    }

    public async Task<DamageCommandResult> ExecuteAsync(string message, PokeCommandOptions? options = null, CancellationToken ct = default)
    {
        try
        {
            var result = await TryExecuteAsync(message, options, ct).ConfigureAwait(false);
            if (result is not null) return result;
            var sets = await _catalog.GetDataSetsAsync(ct).ConfigureAwait(false);
            return Fail("コマンドは " + string.Join(" / ", sets.Select(s => $"!{s.CommandPrefix}")) + " で始めてください。例: " + Usage(sets.LastOrDefault()?.CommandPrefix ?? "pokech"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// このコマンド群のメッセージであれば実行し、そうでなければ null を返す（判定と実行で照合を 1 回にするため）。
    /// 照合（データセット一覧の取得）に失敗した場合はコマンドかどうか判断できないので例外をそのまま投げる。
    /// </summary>
    public async Task<DamageCommandResult?> TryExecuteAsync(string message, PokeCommandOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PokeCommandOptions();
        var match = await MatchAsync(message, ct).ConfigureAwait(false);
        if (match is null) return null;
        try
        {
            var (ds, rest) = match.Value;
            var result = await DispatchAsync(ds, rest, options, ct).ConfigureAwait(false);
            if (result.Message.Length > options.MaxLength)
                return new DamageCommandResult { Success = result.Success, Message = result.Message[..(options.MaxLength - 1)] + "…", Request = result.Request, Damage = result.Damage };
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"失敗しました: {ex.Message}");
        }
    }

    private async Task<DamageCommandResult> DispatchAsync(DataSetInfo ds, string rest, PokeCommandOptions options, CancellationToken ct)
    {
        var tokens = rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (tokens.Count == 0) return Fail(Usage(ds.CommandPrefix));
        var sub = tokens[0].ToLowerInvariant();
        var args = tokens.Skip(1).ToList();
        var isTeam = args.Count > 0 && args[0].Equals("team", StringComparison.OrdinalIgnoreCase);
        if (isTeam) args.RemoveAt(0);
        var data = await _catalog.GetDataSetAsync(ds.Key, ct).ConfigureAwait(false);

        switch (sub)
        {
            case "add" when isTeam: return await AddTeamAsync(ds, data, args, options, ct).ConfigureAwait(false);
            case "add": return await AddAsync(ds, data, args, options, ct).ConfigureAwait(false);
            case "ls" or "list" when isTeam: return await ListTeamsAsync(ds, ct).ConfigureAwait(false);
            case "ls" or "list": return await ListAsync(ds, data, args, ct).ConfigureAwait(false);
            case "more" or "show" or "info" when isTeam: return await MoreTeamAsync(ds, data, args, ct).ConfigureAwait(false);
            case "more" or "show" or "info": return await MoreAsync(ds, data, args, ct).ConfigureAwait(false);
            case "rm" or "del" or "remove" when isTeam: return await RemoveTeamAsync(ds, data, args, options, ct).ConfigureAwait(false);
            case "rm" or "del" or "remove": return await RemoveAsync(ds, args, options, ct).ConfigureAwait(false);
            case "unuse" or "release" or "clear" when isTeam || args.Count == 0: return await UnuseTeamAsync(ds, options, ct).ConfigureAwait(false);
            case "use" when isTeam || args.Count > 0: return await UseTeamAsync(ds, args, options, ct).ConfigureAwait(false);
            case "dmg" or "damage": return await DamageAsync(ds, data, args, options, ct).ConfigureAwait(false);
            case "calc" or "ev": return await CalcAsync(ds, data, args, options, ct).ConfigureAwait(false);
            case "diff" or "speed" or "spd": return await DiffAsync(ds, data, args, options, ct).ConfigureAwait(false);
            case "cmd" or "cmds" or "commands" or "help" or "?" or "ヘルプ":
                return Ok(CommandList(await _catalog.GetDataSetsAsync(ct).ConfigureAwait(false)));
            default: return Fail($"不明なサブコマンド「{tokens[0]}」。{Usage(ds.CommandPrefix)}");
        }
    }

    // ------------------------------------------------------------------ 登録

    private async Task<DamageCommandResult> AddAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("登録は配信者・モデレーターのみ行えます");
        var system = ds.EvSystem;
        if (args.Count < 8)
            return Fail($"書式: !{ds.CommandPrefix} add 名前 H A B C D S 性格 [特性]（{EvRules.Label(system)}は各 0〜{EvRules.MaxPerStat(system)}、合計 {EvRules.MaxTotal(system)} まで）");

        var pokemon = DamageCommand.CreatePokemonResolver(data).Resolve(args[0]);
        if (!pokemon.IsResolved) return Fail(NotFound("ポケモン", args[0], pokemon.Candidates.Select(p => p.Name)));

        var evs = new StatSet();
        var stats = Enum.GetValues<Stat>();
        for (var i = 0; i < 6; i++)
        {
            if (!int.TryParse(args[1 + i], out var v))
                return Fail($"{StatNames.Japanese(stats[i])}の{EvRules.Label(system)}「{args[1 + i]}」が数値ではありません");
            evs[stats[i]] = v;
        }
        if (EvRules.Validate(system, evs) is { } error) return Fail(error);

        var nature = new NameResolver<Nature>(data.Natures, n => n.Name).Resolve(args[7]);
        if (!nature.IsResolved) return Fail(NotFound("性格", args[7], nature.Candidates.Select(n => n.Name)));

        Ability? ability = null;
        if (args.Count > 8)
        {
            var own = data.AbilitiesOf(pokemon.Value!);
            var abilityMatch = new NameResolver<Ability>(own, a => a.Name).Resolve(args[8]);
            if (!abilityMatch.IsResolved)
                return Fail($"{pokemon.Value!.Name}の特性は {string.Join("/", own.Select(a => a.Name))} のいずれかです");
            ability = abilityMatch.Value;
        }
        ability ??= (await UsageOrNullAsync(data, options.Format, ct).ConfigureAwait(false))?.TopAbility(pokemon.Value!) ?? data.AbilitiesOf(pokemon.Value!).FirstOrDefault();

        var entry = await _roster.AddPokemonAsync(ds.Key, pokemon.Value!, evs, nature.Value!, ability, ct).ConfigureAwait(false);
        var build = RosterRepository.ToBuild(entry, data)!;
        return Ok($"#{entry.Id} {pokemon.Value!.Name} を登録しました: {Describe(entry, build)}");
    }

    private async Task<DamageCommandResult> ListAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, CancellationToken ct)
    {
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        IEnumerable<RegisteredPokemon> entries = roster.Pokemon;
        if (args.Count > 0)
        {
            var match = DamageCommand.CreatePokemonResolver(data).Resolve(args[0]);
            if (!match.IsResolved) return Fail(NotFound("ポケモン", args[0], match.Candidates.Select(p => p.Name)));
            entries = entries.Where(e => e.PokemonId == match.Value!.Id);
        }
        var list = entries.ToList();
        if (list.Count == 0) return Ok($"{ds.Name}: 登録されたポケモンはありません");
        return Ok($"{ds.Name} 登録{list.Count}体: " + string.Join(" ", list.Select(e => $"#{e.Id} {e.PokemonName}({e.Nature})")));
    }

    private async Task<DamageCommandResult> MoreAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, CancellationToken ct)
    {
        if (args.Count == 0 || !int.TryParse(args[0], out var id)) return Fail($"書式: !{ds.CommandPrefix} more 登録ID");
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        var entry = roster.Pokemon.FirstOrDefault(e => e.Id == id);
        if (entry is null) return Fail($"登録ID {id} はありません");
        var build = RosterRepository.ToBuild(entry, data);
        if (build is null) return Fail($"#{id} {entry.PokemonName} は現在のデータに存在しません");
        var teams = roster.Teams.Where(t => t.Slots.Any(s => s.EntryId == id)).Select(t => t.Number).ToList();
        var teamText = teams.Count > 0 ? $" チーム{string.Join(",", teams)}" : "";
        return Ok($"#{entry.Id} {Describe(entry, build)}{teamText}");
    }

    private async Task<DamageCommandResult> RemoveAsync(DataSetInfo ds, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("削除は配信者・モデレーターのみ行えます");
        if (args.Count == 0 || !int.TryParse(args[0], out var id)) return Fail($"書式: !{ds.CommandPrefix} rm 登録ID");
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        var entry = roster.Pokemon.FirstOrDefault(e => e.Id == id);
        if (entry is null) return Fail($"登録ID {id} はありません");
        var teamsBefore = roster.Teams.Select(t => t.Number).ToList();
        await _roster.RemovePokemonAsync(ds.Key, id, ct).ConfigureAwait(false);
        var deleted = teamsBefore.Except(roster.Teams.Select(t => t.Number)).ToList();
        var note = deleted.Count > 0 ? $"。0 体になった{string.Join("・", deleted.Select(n => $"チーム{n}"))}を削除しました" : "";
        return Ok($"#{id} {entry.PokemonName} を削除しました（チームからも外しました{note}）");
    }

    // ------------------------------------------------------------------ チーム

    private async Task<DamageCommandResult> AddTeamAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("チーム編集は配信者・モデレーターのみ行えます");
        if (args.Count < 2 || !int.TryParse(args[0], out var teamNo) || !int.TryParse(args[1], out var entryId))
            return Fail($"書式: !{ds.CommandPrefix} add team チーム番号 登録ID [持ち物]");
        Item? item = null;
        if (args.Count > 2)
        {
            var itemMatch = new NameResolver<Item>(data.Items, i => i.Name).Resolve(string.Join(" ", args.Skip(2)));
            if (!itemMatch.IsResolved) return Fail(NotFound("持ち物", args[2], itemMatch.Candidates.Select(i => i.Name)));
            item = itemMatch.Value;
        }
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        var entry = roster.Pokemon.FirstOrDefault(e => e.Id == entryId);
        if (entry is null) return Fail($"登録ID {entryId} はありません");
        var (team, _, replaced) = await _roster.AddToTeamAsync(ds.Key, teamNo, entryId, item, ct).ConfigureAwait(false);
        var itemText = item is null ? "持ち物なし" : $"@{item.Name}";
        return Ok($"チーム{team.Number} に #{entry.Id} {entry.PokemonName}({itemText}) を{(replaced ? "更新" : "追加")}しました（{team.Slots.Count}/6）");
    }

    private async Task<DamageCommandResult> RemoveTeamAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("チーム編集は配信者・モデレーターのみ行えます");
        if (args.Count == 0 || !int.TryParse(args[0], out var teamNo))
            return Fail($"書式: !{ds.CommandPrefix} rm team チーム番号 [ポケモン名]");
        if (args.Count == 1)
        {
            return await _roster.RemoveTeamAsync(ds.Key, teamNo, ct).ConfigureAwait(false)
                ? Ok($"チーム{teamNo} を削除しました")
                : Fail($"チーム{teamNo} はありません");
        }
        var match = DamageCommand.CreatePokemonResolver(data).Resolve(args[1]);
        if (!match.IsResolved) return Fail(NotFound("ポケモン", args[1], match.Candidates.Select(p => p.Name)));
        var removed = await _roster.RemoveFromTeamAsync(ds.Key, teamNo, e => e.PokemonId == match.Value!.Id, ct).ConfigureAwait(false);
        if (!removed) return Fail($"チーム{teamNo} に {match.Value!.Name} はいません");
        var stillThere = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key).Teams.Any(t => t.Number == teamNo);
        return Ok($"チーム{teamNo} から {match.Value!.Name} を外しました" + (stillThere ? "" : "（0 体になったのでチームを削除しました）"));
    }

    private async Task<DamageCommandResult> UnuseTeamAsync(DataSetInfo ds, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("使用チームの変更は配信者・モデレーターのみ行えます");
        return await _roster.ClearActiveTeamAsync(ds.Key, ct).ConfigureAwait(false)
            ? Ok("使用チームを解除しました。以降の dmg / calc / diff は使用率に基づく既定値で計算します")
            : Ok("使用中のチームはありません");
    }

    private async Task<DamageCommandResult> UseTeamAsync(DataSetInfo ds, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("使用チームの変更は配信者・モデレーターのみ行えます");
        if (args.Count == 0 || !int.TryParse(args[0], out var teamNo))
            return Fail($"書式: !{ds.CommandPrefix} use team チーム番号");
        return await _roster.UseTeamAsync(ds.Key, teamNo, ct).ConfigureAwait(false)
            ? Ok($"チーム{teamNo} を使用します。以降の dmg / calc / diff はこのチームの構成で計算します")
            : Fail($"チーム{teamNo} はありません");
    }

    private async Task<DamageCommandResult> ListTeamsAsync(DataSetInfo ds, CancellationToken ct)
    {
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        if (roster.Teams.Count == 0) return Ok($"{ds.Name}: チームはありません");
        var byId = roster.Pokemon.ToDictionary(p => p.Id);
        var parts = roster.Teams.Select(t =>
        {
            var names = t.Slots.Select(s => byId.TryGetValue(s.EntryId, out var p) ? p.PokemonName : $"#{s.EntryId}?");
            var active = roster.ActiveTeam == t.Number ? "(使用中)" : "";
            return $"チーム{t.Number}{active}[{string.Join(",", names)}]";
        });
        return Ok($"{ds.Name}: " + string.Join(" ", parts));
    }

    private async Task<DamageCommandResult> MoreTeamAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, CancellationToken ct)
    {
        if (args.Count == 0 || !int.TryParse(args[0], out var teamNo)) return Fail($"書式: !{ds.CommandPrefix} more team チーム番号");
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        var team = roster.Teams.FirstOrDefault(t => t.Number == teamNo);
        if (team is null) return Fail($"チーム{teamNo} はありません");
        var byId = roster.Pokemon.ToDictionary(p => p.Id);
        var parts = team.Slots.Select(s =>
        {
            if (!byId.TryGetValue(s.EntryId, out var e)) return $"#{s.EntryId}?";
            var item = s.ItemId is { } iid ? data.FindItem(iid)?.Name : null;
            return $"#{e.Id} {e.PokemonName}({e.Nature} {e.EVs.ToShortString()}){(item is null ? "" : "@" + item)}";
        });
        var active = roster.ActiveTeam == teamNo ? "(使用中)" : "";
        return Ok($"チーム{teamNo}{active}: " + string.Join(" / ", parts));
    }

    // ------------------------------------------------------------------ 計算

    private async Task<DamageCommandResult> DamageAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (args.Count < 3) return Fail($"書式: !{ds.CommandPrefix} dmg 攻撃側 技±ランク 防御側 [オプション]");
        var provider = await _roster.ActiveTeamBuildProviderAsync(ds.Key, data, ct).ConfigureAwait(false);
        var text = string.Join(" ", args);

        var normal = await _damage.ExecuteAsync(text, new DamageCommandOptions { DataSetKey = ds.Key, Format = options.Format, BuildProvider = provider, MaxLength = int.MaxValue }, ct).ConfigureAwait(false);
        if (!normal.Success) return normal;
        var crit = await _damage.ExecuteAsync(text, new DamageCommandOptions { DataSetKey = ds.Key, Format = options.Format, BuildProvider = provider, IsCritical = true, MaxLength = int.MaxValue }, ct).ConfigureAwait(false);

        var message = normal.Message;
        if (crit.Success && crit.Damage is not null && crit.Damage.MaxDamage > 0)
            message += $" ｜急所: {crit.Damage.RangeText} {crit.Damage.KnockOut}";

        // 相手（防御側）の特性が不明なら、特性でダメージが変わる場合に特性ごとの結果を添える
        var request = normal.Request!;
        if (provider(request.Defender.Pokemon, false) is null && !MentionsAbility(args, data, request.Defender.Pokemon))
        {
            var variants = AbilityVariants(data, request, normal.Damage!);
            if (variants.Count > 0)
                message += " ｜特性別: " + string.Join(" / ", variants);
        }
        return new DamageCommandResult { Success = true, Message = message, Request = request, Damage = normal.Damage };
    }

    /// <summary>防御側の特性候補ごとに計算し、基準と結果が違うものを「特性名: 結果」で返す。</summary>
    private static List<string> AbilityVariants(PokemonDataSet data, DamageRequest request, DamageResult baseline)
    {
        var variants = new List<string>();
        var abilities = data.AbilitiesOf(request.Defender.Pokemon);
        if (abilities.Count <= 1) return variants;
        var calc = new DamageCalculator(data.TypeChart);
        var distinct = false;
        var lines = new List<string>();
        foreach (var ability in abilities)
        {
            var defender = request.Defender.Clone();
            defender.Ability = ability;
            var result = calc.Calculate(new DamageRequest
            {
                Attacker = request.Attacker, Move = request.Move, Defender = defender,
                Format = request.Format, IsCritical = request.IsCritical, Weather = request.Weather, Terrain = request.Terrain, Screen = request.Screen,
            });
            if (result.MinDamage != baseline.MinDamage || result.MaxDamage != baseline.MaxDamage) distinct = true;
            lines.Add(result.MaxDamage == 0
                ? $"{ability.Name}: 無効"
                : $"{ability.Name}: {result.RangeText} {result.KnockOut}");
        }
        return distinct ? lines : variants;
    }

    private static bool MentionsAbility(IEnumerable<string> args, PokemonDataSet data, Pokemon pokemon)
    {
        var names = new HashSet<string>(data.AbilitiesOf(pokemon).Select(a => NameNormalizer.Normalize(a.Name)));
        return args.Any(a => names.Contains(NameNormalizer.Normalize(a.Replace("防:", "").Replace("攻:", ""))));
    }

    private async Task<DamageCommandResult> CalcAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (args.Count < 4) return Fail($"書式: !{ds.CommandPrefix} calc 攻撃側 技±ランク 防御側 受けたダメージ（例: 93 または 45%）");
        // 数値トークンを探す（4 番目以降の最初の数値または %）
        var damageIndex = -1;
        double observed = 0;
        var asPercent = false;
        for (var i = 3; i < args.Count; i++)
        {
            var pm = PercentToken.Match(args[i]);
            if (pm.Success) { observed = double.Parse(pm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture); asPercent = true; damageIndex = i; break; }
            if (double.TryParse(args[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0) { observed = v; damageIndex = i; break; }
        }
        if (damageIndex < 0) return Fail("受けたダメージを数値（93）か割合（45%）で指定してください");
        args.RemoveAt(damageIndex);

        var provider = await _roster.ActiveTeamBuildProviderAsync(ds.Key, data, ct).ConfigureAwait(false);
        // 防御側は相手なので、使用チームの構成は攻撃側だけに適用する
        Func<Pokemon, bool, PokemonBuild?> attackerOnly = (p, isAttacker) => isAttacker ? provider(p, true) : null;
        var baseline = await _damage.ExecuteAsync(string.Join(" ", args), new DamageCommandOptions
        {
            DataSetKey = ds.Key, Format = options.Format, BuildProvider = attackerOnly, UseUsageDefaults = false, MaxLength = int.MaxValue,
            DefaultDefenderHpEv = 0, DefaultDefenderDefenseEv = 0,
        }, ct).ConfigureAwait(false);
        if (!baseline.Success || baseline.Request is null) return baseline;

        var request = baseline.Request;
        request.Defender.Item = null; // 相手の持ち物は不明
        var estimator = new EvEstimator(data.TypeChart);
        var attackerText = $"{request.Attacker.Pokemon.Name}({request.Attacker.DescribeShort()})";

        // 相手の特性が指定されていなければ、持ちうる特性ごとに推定し、結果が違うときは特性別に出す
        var abilities = MentionsAbility(args, data, request.Defender.Pokemon) || request.Defender.Ability is null
            ? new List<Ability?> { request.Defender.Ability }
            : data.AbilitiesOf(request.Defender.Pokemon).Cast<Ability?>().ToList();
        var results = new List<(Ability? Ability, EvEstimator.Estimate Estimate)>();
        foreach (var ability in abilities)
        {
            var defender = request.Defender.Clone();
            defender.Ability = ability;
            var req = new DamageRequest
            {
                Attacker = request.Attacker, Move = request.Move, Defender = defender,
                Format = request.Format, IsCritical = request.IsCritical, Weather = request.Weather, Terrain = request.Terrain, Screen = request.Screen,
            };
            results.Add((ability, estimator.Run(req, observed, asPercent)));
        }

        string Summary(EvEstimator.Estimate e) => string.Join("|", e.Bands.Select(b => $"{b.Label}{b.MinEv}-{b.MaxEv}")) + $"/{e.MinDefenseStat}-{e.MaxDefenseStat}";
        var allSame = results.Select(r => Summary(r.Estimate)).Distinct().Count() == 1;
        if (allSame || results.Count == 1)
        {
            var e = results[0].Estimate;
            var text = EvEstimator.Format(e, request.Defender.Pokemon, request.Move, ds.EvSystem);
            var abilityNote = results.Count == 1 ? $"特性{results[0].Ability?.Name ?? "不明"}想定" : "特性による差なし";
            return new DamageCommandResult { Success = e.HasResult, Message = $"{attackerText}の{text} ※相手は持ち物なし・{abilityNote}", Request = request };
        }

        var parts = results.Select(r =>
        {
            var e = r.Estimate;
            if (!e.HasResult) return $"{r.Ability?.Name}: 該当なし";
            var unit = ds.EvSystem == EvSystem.Points ? "pt" : "";
            var letter = StatNames.Letter(e.DefenseStat);
            var bands = string.Join(" / ", e.Bands.Select(b => b.MinEv == b.MaxEv ? $"{b.Label}:{letter}{b.MinEv}{unit}" : $"{b.Label}:{letter}{b.MinEv}〜{b.MaxEv}{unit}"));
            var hp = e.HpEvRange is { } h ? $" H{h.MinEv}〜{h.MaxEv}{unit}" : "";
            return $"{r.Ability?.Name}: {bands}{hp}";
        });
        var observedText = asPercent ? $"{observed:0.#}%" : $"{observed:0}";
        var any = results.Any(r => r.Estimate.HasResult);
        return new DamageCommandResult
        {
            Success = any,
            Message = $"{attackerText}の{request.Move.Name}で{request.Defender.Pokemon.Name}に{observedText}ダメージ → 特性別: " + string.Join(" ｜ ", parts) + " ※相手は持ち物なし想定",
            Request = request,
        };
    }

    private async Task<DamageCommandResult> DiffAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (args.Count < 2) return Fail($"書式: !{ds.CommandPrefix} diff 自分のポケモン 相手のポケモン [+1 晴れ まひ 自:まひ など]");
        var resolver = DamageCommand.CreatePokemonResolver(data);
        var mineMatch = resolver.Resolve(args[0]);
        if (!mineMatch.IsResolved) return Fail(NotFound("ポケモン", args[0], mineMatch.Candidates.Select(p => p.Name)));
        var oppMatch = resolver.Resolve(args[1]);
        if (!oppMatch.IsResolved) return Fail(NotFound("ポケモン", args[1], oppMatch.Candidates.Select(p => p.Name)));

        var provider = await _roster.ActiveTeamBuildProviderAsync(ds.Key, data, ct).ConfigureAwait(false);
        var usage = await UsageOrNullAsync(data, options.Format, ct).ConfigureAwait(false);
        var warnings = new List<string>();

        var mine = provider(mineMatch.Value!, true)?.Clone();
        if (mine is null)
        {
            mine = new PokemonBuild(mineMatch.Value!, ds.EvSystem)
            {
                Nature = usage?.TopNature(mineMatch.Value!.Id) ?? data.FindNature("まじめ"),
                Item = usage?.TopItem(mineMatch.Value!.Id),
                Ability = usage?.TopAbility(mineMatch.Value!) ?? data.AbilitiesOf(mineMatch.Value!).FirstOrDefault(),
            };
            mine.EVs[Stat.Speed] = EvRules.Full(ds.EvSystem);
            warnings.Add($"{mine.Pokemon.Name}は使用チームにいないため S{EvRules.Full(ds.EvSystem)}・使用率1位の性格/持ち物で計算");
        }
        var weather = Weather.None;
        var terrain = Terrain.None;
        var opponentParalyzed = false;
        foreach (var token in args.Skip(2))
        {
            var n = NameNormalizer.Normalize(token);
            var (_, rank) = DamageCommand.SplitRankSuffix("x" + token);
            if (rank != 0 && token.Length <= 2) { mine.Boosts[Stat.Speed] = rank; continue; }
            if (n is "まひ" or "相手まひ" or "相手:まひ" or "防:まひ") { opponentParalyzed = true; continue; }
            if (n is "自まひ" or "自分まひ" or "自:まひ" or "攻:まひ" or "自分:まひ") { mine.Status = StatusCondition.Paralysis; continue; }
            if (n is "晴れ" or "はれ") weather = Weather.Sun;
            else if (n is "雨" or "あめ") weather = Weather.Rain;
            else if (n is "砂" or "すなあらし" or "砂嵐") weather = Weather.Sand;
            else if (n is "雪" or "ゆき") weather = Weather.Snow;
            else if (n is "えれき" or "えれきふぃーるど") terrain = Terrain.Electric;
            else warnings.Add($"「{token}」は無視");
        }

        var (mySpeed, myNotes) = SpeedCalculator.Effective(mine, weather, terrain);
        var oppBuild = provider(oppMatch.Value!, false);
        var scarf = data.Items.FirstOrDefault(i => i.Id == 264);
        var reference = SpeedCalculator.OpponentReference(oppMatch.Value!, ds.EvSystem, scarf, opponentParalyzed);
        string text;
        if (oppBuild is not null)
        {
            if (opponentParalyzed) { oppBuild = oppBuild.Clone(); oppBuild.Status = StatusCondition.Paralysis; }
            var (os, oNotes) = SpeedCalculator.Effective(oppBuild, weather, terrain);
            text = SpeedCalculator.Format(mine, mySpeed, myNotes, oppMatch.Value!, oppBuild, os, oNotes, reference);
        }
        else
        {
            text = SpeedCalculator.Format(mine, mySpeed, myNotes, oppMatch.Value!, null, null, null, reference);
        }
        if (warnings.Count > 0) text += " ※" + string.Join("、", warnings);
        return Ok(text);
    }

    // ------------------------------------------------------------------ 共通

    private static async Task<UsageData?> UsageOrNullAsync(PokemonDataSet data, BattleFormat format, CancellationToken ct)
    {
        var usage = await data.GetUsageAsync(format, ct).ConfigureAwait(false);
        return usage.IsEmpty ? null : usage;
    }

    private static string Describe(RegisteredPokemon entry, PokemonBuild build)
    {
        var stats = StatCalculator.CalculateAll(build);
        var ability = build.Ability?.Name ?? "特性不明";
        return $"{entry.PokemonName} {entry.Nature} {entry.EVs.ToShortString()} {ability} 実数値 H{stats.HP}-A{stats.Attack}-B{stats.Defense}-C{stats.SpAttack}-D{stats.SpDefense}-S{stats.Speed}";
    }

    private static string NotFound(string kind, string query, IEnumerable<string> candidates)
    {
        var list = candidates.Take(5).ToList();
        return list.Count > 0
            ? $"{kind}「{query}」が特定できません。候補: {string.Join("、", list)}"
            : $"{kind}「{query}」が見つかりません。";
    }

    private static DamageCommandResult Ok(string message) => new() { Success = true, Message = message };
    private static DamageCommandResult Fail(string message) => new() { Success = false, Message = message };
}
