using System.Text.RegularExpressions;
using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;

namespace FaraPokemonBattleAssistant.Core.Text;

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
/// <item><c>add -p 名前 H A B C D S 性格 [特性]</c> 登録、<c>ls -p [名前]</c> 一覧、<c>detail -p ID</c> 詳細、<c>rm -p ID</c> 削除</item>
/// <item><c>add -t 番号 ID [持ち物]</c>、<c>rm -t 番号 [名前]</c>、<c>use -t 番号</c>、<c>ls -t</c>、<c>detail -t 番号</c>、<c>help</c> 一覧</item>
/// <item><c>dmg 攻撃側 技±ランク 防御側 [オプション]</c> 通常と急所、<c>calc 攻撃側 技±ランク 防御側 受けたダメージ[%]</c> 努力値推定、<c>faster 自分 相手</c> 素早さ比較</item>
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
    /// <summary>calc で分かった相手の振り方（範囲でもよい）。この後の dmg で、防御側に使う（アプリを閉じるまで）。</summary>
    private readonly FoeBook _foes;

    public PokeCommand(DataCatalog catalog, RosterRepository roster) : this(catalog, roster, new FoeBook())
    {
    }

    /// <param name="foes">相手の情報の置き場（画面と共有するときに渡す）。</param>
    public PokeCommand(DataCatalog catalog, RosterRepository roster, FoeBook foes)
    {
        _catalog = catalog;
        _roster = roster;
        _foes = foes;
        _damage = new DamageCommand(catalog);
    }

    public static string Usage(string prefix) =>
        $"!{prefix} add -p 名前 H A B C D S 性格 | ls -p [名前] | detail -p ID | rm -p ID | add -t 番号 ID [持ち物] | rm -t 番号 [名前] | use -t 番号 | unuse -t | ls -t | detail -t 番号 | help | dmg 攻撃 技±ランク 防御 | calc 攻撃 技 防御 ダメージ | faster 自分 相手";

    /// <summary>メッセージがこのコマンド群のものかを判定し、接頭辞と残りを返す。</summary>
    public async Task<(DataSetInfo DataSet, string Arguments)?> MatchAsync(string message, CancellationToken ct = default)
    {
        var text = CommandText.Normalize(message);
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

    /// <summary>データセットを選ばない共通接頭辞（<c>!poke help</c> など）。</summary>
    public const string GenericPrefix = "poke";

    /// <summary>使えるコマンドの一覧（<c>help</c>）。</summary>
    public static string CommandList(IEnumerable<DataSetInfo> dataSets)
    {
        var prefixes = string.Join(" ", dataSets.Select(d => $"!{d.CommandPrefix}={d.Name.Split('（')[0]}"));
        return "ポケモン: add -p 名前 H A B C D S 性格 [特性] / ls -p [名前] / detail -p ID / rm -p ID ｜ " +
               "チーム: add -t 番号 ID [持ち物] / rm -t 番号 [名前] / use -t 番号 / unuse -t / ls -t / detail -t 番号 ｜ " +
               "計算: dmg 攻撃 技±ランク 防御 [努力値 性格 持ち物 特性 急所 やけど どく まひ 天候 …] / calc 攻撃 技 防御 ダメージ[%] / faster 自分 相手 [まひ] ｜ " +
               $"接頭辞: {prefixes}（!poke はチャンピオンズ） ｜ -p/-t は --pokemon/--team でも可（例: use --team 1）";
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
        var tokens = CommandText.Normalize(rest).Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (tokens.Count == 0) return Fail(Usage(ds.CommandPrefix));
        var sub = tokens[0].ToLowerInvariant();
        var args = tokens.Skip(1).ToList();
        // 対象はオプションで指定する: -p / --pokemon（ポケモン）、-t / --team（チーム）。サブコマンドの直後に置く
        var isTeam = args.Count > 0 && (args[0].Equals("-t", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--team", StringComparison.OrdinalIgnoreCase));
        var isPokemon = args.Count > 0 && (args[0].Equals("-p", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--pokemon", StringComparison.OrdinalIgnoreCase));
        if (isTeam || isPokemon) args.RemoveAt(0);
        // ヘルプと参照専用データセットの拒否はデータ本体を使わないので、データセットを読み込む前に返す
        if (sub is "help")
            return Ok(CommandList(await _catalog.GetDataSetsAsync(ct).ConfigureAwait(false)));
        if (!ds.CalcSupported)
            return Fail(NotCalcSupported(ds));
        var data = await _catalog.GetDataSetAsync(ds.Key, ct).ConfigureAwait(false);

        switch (sub)
        {
            case "add" when isTeam: return await AddTeamAsync(ds, data, args, options, ct).ConfigureAwait(false);
            case "add" when isPokemon: return await AddAsync(ds, data, args, options, ct).ConfigureAwait(false);
            case "ls" when isTeam: return await ListTeamsAsync(ds, ct).ConfigureAwait(false);
            case "ls" when isPokemon: return await ListAsync(ds, data, args, ct).ConfigureAwait(false);
            case "detail" when isTeam: return await MoreTeamAsync(ds, data, args, ct).ConfigureAwait(false);
            case "detail" when isPokemon: return await MoreAsync(ds, data, args, ct).ConfigureAwait(false);
            case "rm" when isTeam: return await RemoveTeamAsync(ds, data, args, options, ct).ConfigureAwait(false);
            case "rm" when isPokemon: return await RemoveAsync(ds, args, options, ct).ConfigureAwait(false);
            case "unuse" or "release" or "clear" when isTeam: return await UnuseTeamAsync(ds, options, ct).ConfigureAwait(false);
            case "use" when isTeam: return await UseTeamAsync(ds, args, options, ct).ConfigureAwait(false);
            case "add" or "ls" or "detail" or "rm" or "use" or "unuse" or "release" or "clear":
                return Fail($"「{tokens[0]}」の後に、対象を -p（ポケモン）か -t（チーム）で付けてください（例: !{ds.CommandPrefix} {sub} -p / {sub} -t。--pokemon / --team でも可）。{Usage(ds.CommandPrefix)}");
            case "foe" or "foes" or "opponent": return FoeCommand(ds, data, args);
            case "dmg" or "damage": return await DamageAsync(ds, data, args, options, ct).ConfigureAwait(false);
            case "calc" or "ev": return await CalcAsync(ds, data, args, options, ct).ConfigureAwait(false);
            case "faster": return await DiffAsync(ds, data, args, options, ct).ConfigureAwait(false);
            default: return Fail($"不明なサブコマンド「{tokens[0]}」。{Usage(ds.CommandPrefix)}");
        }
    }

    // ------------------------------------------------------------------ 登録

    private async Task<DamageCommandResult> AddAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("登録は配信者・モデレーターのみ行えます");
        var system = ds.EvSystem;
        if (args.Count < 8)
            return Fail($"書式: !{ds.CommandPrefix} add -p 名前 H A B C D S 性格 [特性]（{EvRules.Label(system)}は各 0〜{EvRules.MaxPerStat(system)}、合計 {EvRules.MaxTotal(system)} まで）");

        var pokemon = DamageCommand.CreatePokemonResolver(data).Resolve(args[0]);
        if (!pokemon.IsResolved) return Fail(NotFound("ポケモン", args[0], pokemon.Candidates.Select(p => p.Name)));

        var evs = new StatSet();
        var stats = Enum.GetValues<Stat>();
        for (var i = 0; i < 6; i++)
        {
            if (!int.TryParse(args[1 + i], out var v))
                return Fail($"{StatNames.Letter(stats[i])}の{EvRules.Label(system)}「{args[1 + i]}」が数値ではありません");
            evs[stats[i]] = v;
        }
        if (EvRules.Validate(system, evs) is { } error) return Fail(error);

        var nature = new NameResolver<Nature>(data.Natures, n => n.Name).Resolve(args[7]);
        if (!nature.IsResolved) return Fail(NotFound("性格", args[7], nature.Candidates.Select(n => n.Name)));

        // 持ち物は登録では指定しない（チームに入れるときに指定する）
        if (args.Skip(8).Any(a => a.StartsWith('@') || a.StartsWith('＠')))
            return Fail($"持ち物はチームに入れるときに指定してください（!{ds.CommandPrefix} add -t チーム番号 登録ID 持ち物）");

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
        return Ok($"{ds.Name} 登録{list.Count}体: " + string.Join(" ", list.Select(e => $"#{e.Id} {e.PokemonName}{NatureParen(data, e.Nature)}")));
    }

    private async Task<DamageCommandResult> MoreAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, CancellationToken ct)
    {
        if (args.Count == 0 || !int.TryParse(args[0], out var id)) return Fail($"書式: !{ds.CommandPrefix} detail -p 登録ID");
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
        if (args.Count == 0 || !int.TryParse(args[0], out var id)) return Fail($"書式: !{ds.CommandPrefix} rm -p 登録ID");
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
            return Fail($"書式: !{ds.CommandPrefix} add -t チーム番号 登録ID [持ち物]");
        Item? item = null;
        if (args.Count > 2)
        {
            var itemMatch = new NameResolver<Item>(data.Items, i => i.Name).Resolve(string.Join(" ", args.Skip(2)));
            if (!itemMatch.IsResolved)
            {
                var askedItem = string.Join(" ", args.Skip(2));
                return await DamageCommand.ItemExistsElsewhereAsync(_catalog, ds.Key, askedItem, ct).ConfigureAwait(false)
                    ? Fail($"持ち物「{askedItem}」は{ds.Name}にありません")
                    : Fail(NotFound("持ち物", args[2], itemMatch.Candidates.Select(i => i.Name)));
            }
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
            return Fail($"書式: !{ds.CommandPrefix} rm -t チーム番号 [ポケモン名]");
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
            ? Ok("使用チームを解除しました。以降の dmg / calc / faster は使用率に基づく既定値で計算します")
            : Ok("使用中のチームはありません");
    }

    private async Task<DamageCommandResult> UseTeamAsync(DataSetInfo ds, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("使用チームの変更は配信者・モデレーターのみ行えます");
        if (args.Count == 0 || !int.TryParse(args[0], out var teamNo))
            return Fail($"書式: !{ds.CommandPrefix} use -t チーム番号");
        return await _roster.UseTeamAsync(ds.Key, teamNo, ct).ConfigureAwait(false)
            ? Ok($"チーム{teamNo} を使用します。以降の dmg / calc / faster はこのチームの構成で計算します")
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
        if (args.Count == 0 || !int.TryParse(args[0], out var teamNo)) return Fail($"書式: !{ds.CommandPrefix} detail -t チーム番号");
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        var team = roster.Teams.FirstOrDefault(t => t.Number == teamNo);
        if (team is null) return Fail($"チーム{teamNo} はありません");
        var byId = roster.Pokemon.ToDictionary(p => p.Id);
        var parts = team.Slots.Select(s =>
        {
            if (!byId.TryGetValue(s.EntryId, out var e)) return $"#{s.EntryId}?";
            var item = s.ItemId is { } iid ? data.FindItem(iid)?.Name : null;
            return $"#{e.Id} {e.PokemonName}({NatureSpace(data, e.Nature)}{e.EVs.ToShortString()}){(item is null ? "" : "@" + item)}";
        });
        var active = roster.ActiveTeam == teamNo ? "(使用中)" : "";
        return Ok($"チーム{teamNo}{active}: " + string.Join(" / ", parts));
    }

    // ------------------------------------------------------------------ 計算

    /// <summary>dmg・calc の攻撃側が登録済みでないときのメッセージ（登録済みなら null）。名前が決まらないときは、それぞれのコマンドの案内に任せる。</summary>
    private static string? RegisteredOnlyError(DataSetInfo ds, PokemonDataSet data, string name, Func<Pokemon, bool, PokemonBuild?> provider, string role)
    {
        var match = DamageCommand.CreatePokemonResolver(data).Resolve(name);
        if (!match.IsResolved) return null;
        return provider(match.Value!, true) is null ? NotRegisteredMessage(ds, match.Value!.Name, role) : null;
    }

    private static string NotRegisteredMessage(DataSetInfo ds, string pokemonName, string role) =>
        $"{role}の{pokemonName}は登録されていません。!{ds.CommandPrefix} dmg・calc・faster は、登録済みのポケモンだけ使えます（!{ds.CommandPrefix} add -p で登録してください）";

    private async Task<DamageCommandResult> DamageAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (args.Count < 3) return Fail($"書式: !{ds.CommandPrefix} dmg 攻撃側 技±ランク 防御側 [オプション]");
        var provider = await _roster.RegisteredBuildProviderAsync(ds.Key, data, ct).ConfigureAwait(false);
        if (RegisteredOnlyError(ds, data, args[0], provider, "攻撃側") is { } notRegistered) return Fail(notRegistered);
        var text = string.Join(" ", args);

        // 1 つ目の想定（相手の情報が範囲なら、耐久が最も低い想定）
        var first = await DamageScenarioAsync(ds, data, args, text, options, provider, useHigh: false, ct).ConfigureAwait(false);
        if (first.Failure is not null) return first.Failure;

        var foe = _foes.Get(ds.Key, first.Request!.Defender.Pokemon.Id);
        // 分かっている振り方は、その技が使う防御（B か D）が同じときだけ使う
        if (foe is not null && foe.DefenseStat != first.Request.Move.DefenseStatUsed) foe = null;
        if (foe is null || !foe.IsRange)
            return new DamageCommandResult { Success = true, Message = DamageCommand.Header(first.Request, first.Last!) + "\n" + first.Body + first.Note, Request = first.Request, Damage = first.Last };

        // 相手の情報が範囲のときは、耐久が最も低い想定と最も高い想定の 2 つを出す
        var second = await DamageScenarioAsync(ds, data, args, text, options, provider, useHigh: true, ct).ConfigureAwait(false);
        if (second.Failure is not null) return second.Failure;
        var header = DamageCommand.Header(first.Request, first.Last!, $"{foe.Pokemon.Name}({foe.Summary})");
        return new DamageCommandResult
        {
            Success = true,
            Message = header + "\n" + first.Body + "\n" + second.Body + first.Note,
            Request = first.Request,
            Damage = first.Last,
        };
    }

    /// <summary>dmg の 1 つの想定（相手の情報があれば、耐久が最も低い／高い想定の構成）の結果の行。</summary>
    private async Task<(DamageCommandResult? Failure, DamageRequest? Request, DamageResult? Last, string Body, string Note)> DamageScenarioAsync(
        DataSetInfo ds, PokemonDataSet data, List<string> args, string text, PokeCommandOptions options,
        Func<Pokemon, bool, PokemonBuild?> provider, bool useHigh, CancellationToken ct)
    {
        // 登録の構成は攻撃側だけに使う（防御側は相手なので、分かっている相手の情報があればそれ、なければ使用率の既定値で計算する）
        var ignoreFoe = false;
        PokemonBuild? FoeBuild(Pokemon p) => !ignoreFoe && _foes.Get(ds.Key, p.Id) is { } known ? (useHigh ? known.High : known.Low).Clone() : null;
        Func<Pokemon, bool, PokemonBuild?> withFoe = (p, isAttacker) => isAttacker ? provider(p, true) : FoeBuild(p);
        string? scenarioLabel = null;

        // 防御側は相手なので、既定は HP と受ける側の防御（物理技なら B、特殊技なら D）に全振りで計算する
        DamageCommandOptions Opts(int? hitCount = null) => new()
        {
            DataSetKey = ds.Key, Format = options.Format, BuildProvider = withFoe, MaxLength = int.MaxValue,
            DefaultDefenderHpEv = 252, DefaultDefenderDefenseEv = 252, HitCount = hitCount,
        };
        var normal = await _damage.ExecuteAsync(text, Opts(), ct).ConfigureAwait(false);
        if (!normal.Success || normal.Request is null || normal.Damage is null) return (normal, null, null, "", "");
        var known = _foes.Get(ds.Key, normal.Request.Defender.Pokemon.Id);
        // その技が使う防御（B か D）と、分かっている振り方の防御が違うときは、相手の情報を使わず、既定（全振り）で計算し直す
        if (known is not null && known.DefenseStat != normal.Request.Move.DefenseStatUsed)
        {
            ignoreFoe = true;
            known = null;
            normal = await _damage.ExecuteAsync(text, Opts(), ct).ConfigureAwait(false);
            if (!normal.Success || normal.Request is null || normal.Damage is null) return (normal, null, null, "", "");
        }
        var request = normal.Request;
        // 範囲のときは、使った防御の文字（B / D）で「B最小想定」「B最大想定」と出す
        if (known is not null && known.IsRange) scenarioLabel = $"{StatNames.Letter(known.DefenseStat)}{(useHigh ? "最大" : "最小")}想定";

        // 連続技は、当たった回数ごと（そこまでの合計）を出す。おやこあいは 1 回目・2 回目（合計）
        // 急所は、オプション「急所」を付けたときだけ（付けると、すべて急所のダメージ）
        int[]? counts = null;
        if (MoveEffects.IsEscalating(request.Move)) counts = new[] { 1, 2, 3 };
        else if (MoveEffects.HitsTwoToFive(request.Move)) counts = request.Attacker.Ability?.Identifier == "skill-link" ? new[] { 5 } : new[] { 1, 2, 3, 4, 5 };
        else if (normal.Damage.ParentalBond) counts = new[] { 1, 2 };

        var tags = new List<string>();
        if (request.IsCritical) tags.Add("急所");
        if (scenarioLabel is not null) tags.Add(scenarioLabel);
        var label = tags.Count > 0 ? $"結果（{string.Join("・", tags)}）" : "結果";
        var last = normal.Damage;
        string body;
        if (counts is null)
        {
            body = $"{label} {normal.Damage.RangeText} {normal.Damage.KnockOut}";
        }
        else
        {
            var parts = new List<string>();
            foreach (var n in counts)
            {
                var r = await _damage.ExecuteAsync(text, Opts(n), ct).ConfigureAwait(false);
                if (!r.Success || r.Damage is null) return (r, null, null, "", "");
                last = r.Damage;
                parts.Add($"{n}回{(counts.Length == 1 ? "（スキルリンク）" : "目")}: {r.Damage.RangeText} {r.Damage.KnockOut}");
            }
            body = label + " " + string.Join(" | ", parts);
        }

        // 連続技でないときは、特性でダメージが変わる場合の結果を添える（相手の特性が不明なら特性別、攻撃側は HP・状態・天候による変化）
        if (counts is null)
        {
            if (!MentionsAbility(args, data, request.Defender.Pokemon))
            {
                var variants = AbilityVariants(data, request, normal.Damage);
                if (variants.Count > 0)
                    body += " ｜特性別: " + string.Join(" / ", variants);
            }
            if (AttackerAbilityVariants(data, request, normal.Damage) is { } attackerVariants) body += attackerVariants;
        }
        // 無視したオプションなどの注意（※…）は、最後に添える
        var noteAt = normal.Message.IndexOf(" ※", StringComparison.Ordinal);
        var note = noteAt >= 0 ? normal.Message[noteAt..] : "";
        return (null, request, last, body, note);
    }

    /// <summary>相手の情報（calc で分かった振り方）の一覧・解除。<c>foe ls</c>、<c>foe clear [ポケモン名]</c>。</summary>
    private DamageCommandResult FoeCommand(DataSetInfo ds, PokemonDataSet data, List<string> args)
    {
        var sub = args.Count > 0 ? args[0].ToLowerInvariant() : "ls";
        if (sub is "ls" or "list")
        {
            var list = _foes.List(ds.Key);
            if (list.Count == 0) return new DamageCommandResult { Success = true, Message = "分かっている相手の情報はありません（calc で推定すると登録されます）" };
            return new DamageCommandResult { Success = true, Message = "相手の情報: " + string.Join(" ｜ ", list.Select(f => $"{f.Pokemon.Name}({f.Summary})")) };
        }
        if (sub is "clear" or "rm" or "release")
        {
            if (args.Count < 2)
            {
                var count = _foes.Clear(ds.Key);
                return new DamageCommandResult { Success = true, Message = count == 0 ? "解除する相手の情報はありません" : $"相手の情報を {count} 件すべて解除しました" };
            }
            var match = DamageCommand.CreatePokemonResolver(data).Resolve(string.Join(" ", args.Skip(1)));
            if (!match.IsResolved) return Fail($"ポケモン「{string.Join(" ", args.Skip(1))}」が見つかりません");
            return _foes.Remove(ds.Key, match.Value!.Id)
                ? new DamageCommandResult { Success = true, Message = $"{match.Value.Name}の相手の情報を解除しました" }
                : new DamageCommandResult { Success = true, Message = $"{match.Value.Name}の相手の情報はありません" };
        }
        return Fail($"書式: !{ds.CommandPrefix} foe ls | foe clear [ポケモン名]");
    }

    /// <summary>
    /// 攻撃側の特性が、状態によってダメージを変えるとき（HP が減る・状態異常・天候）、その状態の結果を「｜〜時: …」で並べて返す。
    /// 今の条件ですでに満たしているもの・結果が変わらないものは出さない。
    /// </summary>
    private static string? AttackerAbilityVariants(PokemonDataSet data, DamageRequest request, DamageResult baseline)
    {
        var ability = request.Attacker.Ability;
        if (ability is null) return null;
        var calc = new DamageCalculator(data.TypeChart, data.Info.Generation);
        var attacker = request.Attacker;
        var candidates = new List<(string Label, DamageRequest Request)>();

        DamageRequest WithHp(int percent)
        {
            var clone = attacker.Clone();
            clone.HpPercent = percent;
            return request.With(attacker: clone);
        }
        DamageRequest WithStatus(StatusCondition status)
        {
            var clone = attacker.Clone();
            clone.Status = status;
            return request.With(attacker: clone);
        }

        switch (ability.Identifier)
        {
            case "blaze" or "overgrow" or "torrent" or "swarm":
                // HP 1/3 以下で、対応するタイプの技が強くなる
                if (attacker.CurrentHp * 3 > attacker.MaxHp) candidates.Add(($"{ability.Name}発動時(HP1/3以下)", WithHp(33)));
                break;
            case "defeatist":
                if (attacker.CurrentHp * 2 > attacker.MaxHp) candidates.Add(($"{ability.Name}発動時(HP半分以下)", WithHp(50)));
                break;
            case "guts":
                if (attacker.Status == StatusCondition.None) candidates.Add(($"{ability.Name}発動時(状態異常)", WithStatus(StatusCondition.Poison)));
                break;
            case "toxic-boost":
                if (attacker.Status is not (StatusCondition.Poison or StatusCondition.BadlyPoisoned)) candidates.Add(($"{ability.Name}発動時(どく)", WithStatus(StatusCondition.Poison)));
                break;
            case "flare-boost":
                if (attacker.Status != StatusCondition.Burn) candidates.Add(($"{ability.Name}発動時(やけど)", WithStatus(StatusCondition.Burn)));
                break;
            case "solar-power":
                if (request.Weather != Weather.Sun) candidates.Add(($"{ability.Name}発動時(晴れ)", request.With(weather: Weather.Sun)));
                break;
            case "sand-force":
                if (request.Weather != Weather.Sand) candidates.Add(($"{ability.Name}発動時(砂嵐)", request.With(weather: Weather.Sand)));
                break;
        }

        var parts = new List<string>();
        foreach (var (label, variant) in candidates)
        {
            var result = calc.Calculate(variant);
            if (result.MinDamage == baseline.MinDamage && result.MaxDamage == baseline.MaxDamage) continue;
            parts.Add($" ｜{label}: {result.RangeText} {result.KnockOut}");
        }
        return parts.Count == 0 ? null : string.Concat(parts);
    }

    /// <summary>防御側の特性候補ごとに計算し、基準と結果が違うものを「特性名: 結果」で返す。</summary>
    private static List<string> AbilityVariants(PokemonDataSet data, DamageRequest request, DamageResult baseline)
    {
        var variants = new List<string>();
        var abilities = data.AbilitiesOf(request.Defender.Pokemon);
        if (abilities.Count <= 1) return variants;
        var calc = new DamageCalculator(data.TypeChart, data.Info.Generation);
        var distinct = false;
        var lines = new List<string>();
        foreach (var ability in abilities)
        {
            var defender = request.Defender.Clone();
            defender.Ability = ability;
            var result = calc.Calculate(request.With(defender: defender));
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

        var provider = await _roster.RegisteredBuildProviderAsync(ds.Key, data, ct).ConfigureAwait(false);
        if (args.Count > 0 && RegisteredOnlyError(ds, data, args[0], provider, "攻撃側") is { } notRegistered) return Fail(notRegistered);
        // 防御側は相手なので、登録の構成は攻撃側だけに適用する
        Func<Pokemon, bool, PokemonBuild?> attackerOnly = (p, isAttacker) => isAttacker ? provider(p, true) : null;
        var baseline = await _damage.ExecuteAsync(string.Join(" ", args), new DamageCommandOptions
        {
            DataSetKey = ds.Key, Format = options.Format, BuildProvider = attackerOnly, UseUsageDefaults = false, MaxLength = int.MaxValue,
            DefaultDefenderHpEv = 0, DefaultDefenderDefenseEv = 0,
        }, ct).ConfigureAwait(false);
        if (!baseline.Success || baseline.Request is null) return baseline;

        var request = baseline.Request;
        request.Defender.Item = null; // 相手の持ち物は不明
        var estimator = new EvEstimator(data.TypeChart, ds.Generation);
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
            var req = request.With(defender: defender);
            results.Add((ability, estimator.Run(req, observed, asPercent)));
        }

        string Summary(EvEstimator.Estimate e) => string.Join("|", e.Bands.Select(b => $"{b.Label}{b.MinEv}-{b.MaxEv}")) + $"/{e.MinDefenseStat}-{e.MaxDefenseStat}";
        var allSame = results.Select(r => Summary(r.Estimate)).Distinct().Count() == 1;
        if (allSame || results.Count == 1)
        {
            var e = results[0].Estimate;
            var text = EvEstimator.Format(e, request.Defender.Pokemon, request.Move, ds.EvSystem);
            var abilityNote = results.Count == 1 ? $"特性{results[0].Ability?.Name ?? "不明"}想定" : "特性による差なし";
            // 分かった振り方は（範囲でも）覚えておき、この後の dmg の防御側に使う
            var remembered = "";
            if (results.Count == 1 || allSame)
            {
                var foe = KnownFoe.FromEstimate(ds.Key, e, request.Defender.Pokemon, ds.EvSystem, data.Natures);
                if (foe is not null)
                {
                    _foes.Set(foe);
                    remembered = $" ※この情報を、この後の dmg の{foe.Pokemon.Name}に使います（解除: !{ds.CommandPrefix} foe clear {foe.Pokemon.Name}）";
                }
            }
            return new DamageCommandResult { Success = e.HasResult, Message = $"{attackerText}の{text} ※相手は持ち物なし・{abilityNote}{remembered}", Request = request };
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
        // 特性で分かれたときも、すべての特性の結果をまとめた範囲として覚える
        var merged = KnownFoe.Merge(results.Where(r => r.Estimate.HasResult)
            .Select(r => KnownFoe.FromEstimate(ds.Key, r.Estimate, request.Defender.Pokemon, ds.EvSystem, data.Natures)).OfType<KnownFoe>().ToList());
        var rememberedAll = "";
        if (merged is not null)
        {
            _foes.Set(merged);
            rememberedAll = $" ※この情報を、この後の dmg の{merged.Pokemon.Name}に使います（解除: !{ds.CommandPrefix} foe clear {merged.Pokemon.Name}）";
        }
        return new DamageCommandResult
        {
            Success = any,
            Message = $"{attackerText}の{request.Move.Name}で{request.Defender.Pokemon.Name}に{observedText}ダメージ → 特性別: " + string.Join(" ｜ ", parts) + " ※相手は持ち物なし想定" + rememberedAll,
            Request = request,
        };
    }

    private async Task<DamageCommandResult> DiffAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (args.Count < 2) return Fail($"書式: !{ds.CommandPrefix} faster 自分のポケモン 相手のポケモン [+1 晴れ まひ 自:まひ など]");
        var resolver = DamageCommand.CreatePokemonResolver(data);
        var mineMatch = resolver.Resolve(args[0]);
        if (!mineMatch.IsResolved) return Fail(NotFound("ポケモン", args[0], mineMatch.Candidates.Select(p => p.Name)));
        var oppMatch = resolver.Resolve(args[1]);
        if (!oppMatch.IsResolved) return Fail(NotFound("ポケモン", args[1], oppMatch.Candidates.Select(p => p.Name)));

        var provider = await _roster.RegisteredBuildProviderAsync(ds.Key, data, ct).ConfigureAwait(false);
        var usage = await UsageOrNullAsync(data, options.Format, ct).ConfigureAwait(false);
        var warnings = new List<string>();

        // 自分のポケモンは、登録済みのものに限る（特性・持ち物・努力値は登録の内容）
        var mine = provider(mineMatch.Value!, true)?.Clone();
        if (mine is null) return Fail(NotRegisteredMessage(ds, mineMatch.Value!.Name, "自分のポケモン"));
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

        var (mySpeed, myNotes) = SpeedCalculator.Effective(mine, weather, terrain, generation: ds.Generation);
        var oppBuild = provider(oppMatch.Value!, false);
        var scarf = data.Items.FirstOrDefault(i => i.Id == 264);
        var reference = SpeedCalculator.OpponentReference(oppMatch.Value!, ds.EvSystem, oppMatch.Value!.IsMega ? null : scarf, opponentParalyzed, ds.Generation);
        string text;
        if (oppBuild is not null)
        {
            if (opponentParalyzed) { oppBuild = oppBuild.Clone(); oppBuild.Status = StatusCondition.Paralysis; }
            var (os, oNotes) = SpeedCalculator.Effective(oppBuild, weather, terrain, generation: ds.Generation);
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

    /// <summary>返信用の性格表示（<c>C↑A↓</c>）。補正のない性格・不明な性格は空。</summary>
    private static string NatureSummary(PokemonDataSet data, string name) => data.FindNature(name)?.Summary() ?? "";
    private static string NatureParen(PokemonDataSet data, string name) => NatureSummary(data, name) is { Length: > 0 } t ? $"({t})" : "";
    private static string NatureSpace(PokemonDataSet data, string name) => NatureSummary(data, name) is { Length: > 0 } t ? t + " " : "";

    private static string Describe(RegisteredPokemon entry, PokemonBuild build)
    {
        var stats = StatCalculator.CalculateAll(build);
        var ability = build.Ability?.Name ?? "特性不明";
        return $"{entry.PokemonName} {(build.Nature?.Summary() is { } nt ? nt + " " : "")}{entry.EVs.ToShortString()} {ability} 実数値 H{stats.HP}-A{stats.Attack}-B{stats.Defense}-C{stats.SpAttack}-D{stats.SpDefense}-S{stats.Speed}";
    }

    /// <summary>計算に対応していないゲーム（昔の世代）を指定されたときの案内。</summary>
    public static string NotCalcSupported(DataSetInfo ds) =>
        $"{ds.Name} はダメージ計算・素早さ比較・登録に対応していません（サイトのポケモン一覧・タイプ相性表で見られます）";

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
