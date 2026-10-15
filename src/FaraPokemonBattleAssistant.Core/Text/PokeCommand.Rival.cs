using System.Text.RegularExpressions;
using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;

namespace FaraPokemonBattleAssistant.Core.Text;

/// <summary>
/// 相手ポケモン（<c>-r</c> / <c>--rival</c>）と、終わった試合の履歴（<c>-h</c> / <c>--history</c>）、自分のポケモンの更新（<c>set -m</c>）、
/// ダメージからの推定（<c>scan</c>）のコマンド。
/// </summary>
public sealed partial class PokeCommand
{
    /// <summary>対戦中に登録できる相手ポケモンの数（1 チーム 6 体）。</summary>
    public const int MaxRivals = 6;

    private static readonly Regex RivalEvToken = new(@"^(?:[HABCDShabcds]\d{1,3})+$", RegexOptions.Compiled);
    private static readonly Regex RivalEvPair = new(@"([HABCDShabcds])(\d{1,3})", RegexOptions.Compiled);

    /// <summary>コマンドで指定したポケモン 1 体分の内容（分かった部分だけ）。</summary>
    private sealed class RivalSpec
    {
        public Pokemon Pokemon { get; init; } = null!;
        public Dictionary<Stat, int> Evs { get; } = new();
        public Nature? Nature { get; set; }
        public Ability? Ability { get; set; }

        public bool HasInfo => Evs.Count > 0 || Nature is not null || Ability is not null;
    }

    /// <summary>
    /// 名前と、その後ろに付く振り方（<c>H32 D10</c> または 6 つの数字）・性格（名前か <c>B↑A↓</c>）・特性を読む。
    /// 名前が出るたびに次のポケモンの指定になる（<c>add -r カイリュー ハバタクカミ</c> のようにまとめて指定できる）。
    /// </summary>
    private static (List<RivalSpec> Specs, string? Error) ParseSpecs(IReadOnlyList<string> tokens, PokemonDataSet data, EvSystem system)
    {
        var specs = new List<RivalSpec>();
        var pokemonResolver = DamageCommand.CreatePokemonResolver(data);
        var natureResolver = new NameResolver<Nature>(data.Natures, n => n.Name);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var current = specs.LastOrDefault();

            // 6 つの数字（H A B C D S の順）
            if (current is not null && i + 5 < tokens.Count && Enumerable.Range(i, 6).All(k => int.TryParse(tokens[k], out _)))
            {
                var stats = Enum.GetValues<Stat>();
                for (var k = 0; k < 6; k++) current.Evs[stats[k]] = int.Parse(tokens[i + k]);
                i += 5;
                continue;
            }
            // H32 D10 のような指定
            if (RivalEvToken.IsMatch(token))
            {
                if (current is null) return (specs, $"「{token}」の前に、ポケモン名を書いてください");
                foreach (Match pair in RivalEvPair.Matches(token))
                    current.Evs[StatNames.Parse(pair.Groups[1].Value.ToUpperInvariant())!.Value] = int.Parse(pair.Groups[2].Value);
                continue;
            }
            // B↑A↓ のような性格
            if (DamageCommand.TryParseNatureArrow(token, data.Natures, out var arrowNature))
            {
                if (current is null) return (specs, $"「{token}」の前に、ポケモン名を書いてください");
                if (arrowNature is null) return (specs, $"「{token}」に合う性格がありません");
                current.Nature = arrowNature;
                continue;
            }
            // 性格の名前
            var nature = natureResolver.Resolve(token);
            if (nature.IsResolved && current is not null)
            {
                current.Nature = nature.Value;
                continue;
            }
            // 特性（直前のポケモンの特性）
            if (current is not null)
            {
                var own = data.AbilitiesOf(current.Pokemon);
                var ability = new NameResolver<Ability>(own, a => a.Name).Resolve(token);
                if (ability.IsResolved)
                {
                    current.Ability = ability.Value;
                    continue;
                }
            }
            // 次のポケモン
            var pokemon = pokemonResolver.Resolve(token);
            if (pokemon.IsResolved)
            {
                specs.Add(new RivalSpec { Pokemon = pokemon.Value! });
                continue;
            }
            return (specs, current is null ? NotFound("ポケモン", token, pokemon.Candidates.Select(p => p.Name))
                : $"「{token}」が{current.Pokemon.Name}の振り方・性格・特性・次のポケモン名として読めません");
        }
        // ポイントの検証
        foreach (var spec in specs)
        {
            foreach (var (stat, value) in spec.Evs)
            {
                if (value < 0 || value > EvRules.MaxPerStat(system))
                    return (specs, $"{spec.Pokemon.Name}の{StatNames.Letter(stat)}は 0〜{EvRules.MaxPerStat(system)} にしてください");
            }
            if (spec.Evs.Values.Sum() > EvRules.MaxTotal(system))
                return (specs, $"{spec.Pokemon.Name}の{EvRules.Label(system)}の合計が {EvRules.MaxTotal(system)} を超えています");
        }
        return (specs, null);
    }

    /// <summary>指定した内容を相手ポケモンに反映する（指定した項目だけ上書き）。</summary>
    private static void ApplySpec(RivalPokemon rival, RivalSpec spec, DateTimeOffset now)
    {
        foreach (var (stat, value) in spec.Evs)
        {
            switch (stat)
            {
                case Stat.HP: rival.HP = value; break;
                case Stat.Attack: rival.Attack = value; break;
                case Stat.Defense: rival.Defense = value; break;
                case Stat.SpAttack: rival.SpAttack = value; break;
                case Stat.SpDefense: rival.SpDefense = value; break;
                case Stat.Speed: rival.Speed = value; break;
            }
        }
        if (spec.Nature is not null) rival.Nature = spec.Nature.Name;
        if (spec.Ability is not null) rival.AbilityId = spec.Ability.Id;
        rival.Updated = now.ToString("o");
    }

    // ------------------------------------------------------------------ 相手ポケモン

    /// <summary>add -r 名前 [振り方] [性格] [特性] （名前はまとめて指定できる）。同じポケモンは 1 体で、2 回目以降は更新。</summary>
    private async Task<DamageCommandResult> AddRivalAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("相手ポケモンの登録は配信者・モデレーターのみ行えます");
        if (args.Count == 0) return Fail($"書式: !{ds.CommandPrefix} add -r 名前 [名前 …] または add -r 名前 H A B C D S 性格 [特性]（一部だけなら H32 D10 B↑A↓ のように）");
        var (specs, error) = ParseSpecs(args, data, ds.EvSystem);
        if (error is not null) return Fail(error);
        if (specs.Count == 0) return Fail("ポケモン名を書いてください");
        return await UpsertRivalsAsync(ds, data, specs, "登録", ct).ConfigureAwait(false);
    }

    /// <summary>set -r 名前 [振り方] [性格] [特性] 。登録済みの相手の、指定した項目だけ更新（まだなら登録）。</summary>
    private async Task<DamageCommandResult> SetRivalAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("相手ポケモンの更新は配信者・モデレーターのみ行えます");
        if (args.Count < 2) return Fail($"書式: !{ds.CommandPrefix} set -r 名前 [H32 D10 …] [性格 / B↑A↓] [特性]");
        var (specs, error) = ParseSpecs(args, data, ds.EvSystem);
        if (error is not null) return Fail(error);
        if (specs.Count == 0 || specs.All(s => !s.HasInfo)) return Fail("更新する内容（振り方・性格・特性）を書いてください");
        return await UpsertRivalsAsync(ds, data, specs, "更新", ct).ConfigureAwait(false);
    }

    private async Task<DamageCommandResult> UpsertRivalsAsync(DataSetInfo ds, PokemonDataSet data, List<RivalSpec> specs, string verb, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var lines = new List<string>();
        string? tooMany = null;
        await _roster.ChangeRivalsAsync(ds.Key, roster =>
        {
            lines.Clear();
            tooMany = null;
            foreach (var spec in specs)
            {
                var rival = roster.Rivals.FirstOrDefault(r => r.PokemonId == spec.Pokemon.Id);
                var created = rival is null;
                if (rival is null)
                {
                    if (roster.Rivals.Count >= MaxRivals) { tooMany = $"対戦中の相手が {MaxRivals} 体います。試合が終わったら !{ds.CommandPrefix} clear -r で履歴に移してください（{spec.Pokemon.Name}は登録できませんでした）"; continue; }
                    rival = new RivalPokemon { Id = roster.NextRivalId++, PokemonId = spec.Pokemon.Id, PokemonName = spec.Pokemon.Name };
                    roster.Rivals.Add(rival);
                }
                ApplySpec(rival, spec, now);
                rival.Updated = now.ToString("o");
                lines.Add($"{(created ? "登録" : verb == "登録" ? "更新" : "更新")} {DescribeRival(rival, data, ds.EvSystem)}");
            }
            return (0, lines.Count > 0);
        }, ct).ConfigureAwait(false);
        if (lines.Count == 0) return Fail(tooMany ?? "登録できませんでした");
        return Ok("相手: " + string.Join(" ｜ ", lines) + (tooMany is null ? "" : $" ※{tooMany}"));
    }

    /// <summary>ls -r: 対戦中の相手。</summary>
    private async Task<DamageCommandResult> ListRivalsAsync(DataSetInfo ds, PokemonDataSet data, CancellationToken ct)
    {
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        if (roster.Rivals.Count == 0) return Ok($"{ds.Name}: 対戦中の相手はいません（!{ds.CommandPrefix} add -r 名前 で登録）");
        return Ok($"対戦中の相手 {roster.Rivals.Count}体: " + string.Join(" ｜ ", roster.Rivals.Select(r => DescribeRival(r, data, ds.EvSystem))));
    }

    /// <summary>detail -r 名前: 相手 1 体の詳細（推定の根拠つき）。</summary>
    private async Task<DamageCommandResult> DetailRivalAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, CancellationToken ct)
    {
        if (args.Count == 0) return Fail($"書式: !{ds.CommandPrefix} detail -r 名前");
        var match = DamageCommand.CreatePokemonResolver(data).Resolve(args[0]);
        if (!match.IsResolved) return Fail(NotFound("ポケモン", args[0], match.Candidates.Select(p => p.Name)));
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        var rival = roster.Rivals.FirstOrDefault(r => r.PokemonId == match.Value!.Id);
        if (rival is null) return Fail($"{match.Value!.Name}は対戦中の相手にいません");
        return Ok(DescribeRival(rival, data, ds.EvSystem, withNotes: true));
    }

    /// <summary>rm -r 名前: 登録ミスなどの取り消し（履歴には残さない）。</summary>
    private async Task<DamageCommandResult> RemoveRivalAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("相手ポケモンの削除は配信者・モデレーターのみ行えます");
        if (args.Count == 0) return Fail($"書式: !{ds.CommandPrefix} rm -r 名前");
        var match = DamageCommand.CreatePokemonResolver(data).Resolve(args[0]);
        if (!match.IsResolved) return Fail(NotFound("ポケモン", args[0], match.Candidates.Select(p => p.Name)));
        var removed = await _roster.ChangeRivalsAsync(ds.Key, roster => { var n = roster.Rivals.RemoveAll(r => r.PokemonId == match.Value!.Id); return (n > 0, n > 0); }, ct).ConfigureAwait(false);
        return removed ? Ok($"{match.Value!.Name}を対戦中の相手から外しました（履歴には残しません）") : Fail($"{match.Value!.Name}は対戦中の相手にいません");
    }

    /// <summary>clear -r [メモ]: 試合を終える。何か分かっている相手だけ履歴に移し、対戦中の相手は空にする。</summary>
    private async Task<DamageCommandResult> EndMatchAsync(DataSetInfo ds, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("試合の終了は配信者・モデレーターのみ行えます");
        var ended = await _roster.EndMatchAsync(ds.Key, string.Join(" ", args), DateTimeOffset.Now, ct).ConfigureAwait(false);
        if (ended.Match is null)
            return Ok(ended.Dropped == 0 ? "対戦中の相手はいません" : $"対戦中の相手 {ended.Dropped} 体は名前だけだったので、履歴には移さず空にしました");
        var dropped = ended.Dropped > 0 ? $"（名前だけの {ended.Dropped} 体は移していません）" : "";
        var memo = ended.Match.Memo.Length > 0 ? $" 「{ended.Match.Memo}」" : "";
        return Ok($"試合を終えました。履歴 #{ended.Match.Id}{memo} に {ended.Moved} 体を移しました{dropped}: {string.Join("・", ended.Match.Foes.Select(f => f.PokemonName))}");
    }

    // ------------------------------------------------------------------ 履歴

    /// <summary>ls -h [名前]: 試合の履歴（名前を付けると、そのポケモンがいた試合の振り方）。</summary>
    private async Task<DamageCommandResult> ListHistoryAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, CancellationToken ct)
    {
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        if (roster.History.Count == 0) return Ok($"{ds.Name}: 試合の履歴はありません（!{ds.CommandPrefix} clear -r で、対戦中の相手を履歴に移せます）");
        if (args.Count > 0)
        {
            var match = DamageCommand.CreatePokemonResolver(data).Resolve(args[0]);
            if (!match.IsResolved) return Fail(NotFound("ポケモン", args[0], match.Candidates.Select(p => p.Name)));
            var rows = roster.History.OrderByDescending(m => m.Id)
                .Select(m => (Match: m, Foe: m.Foes.FirstOrDefault(f => f.PokemonId == match.Value!.Id)))
                .Where(x => x.Foe is not null).Take(8).ToList();
            if (rows.Count == 0) return Ok($"{match.Value!.Name}は履歴にいません");
            return Ok($"{match.Value!.Name}の履歴 {rows.Count}件: " + string.Join(" ｜ ", rows.Select(x => $"#{x.Match.Id} {DateText(x.Match.Date)}{MemoText(x.Match.Memo)} {DescribeRival(x.Foe!, data, ds.EvSystem, withName: false)}")));
        }
        var recent = roster.History.OrderByDescending(m => m.Id).Take(8).ToList();
        return Ok($"試合の履歴 {roster.History.Count}件（新しい順）: " + string.Join(" ｜ ", recent.Select(m => $"#{m.Id} {DateText(m.Date)}{MemoText(m.Memo)} {string.Join("・", m.Foes.Select(f => f.PokemonName))}")));
    }

    /// <summary>detail -h 履歴ID: その試合の相手ポケモンと推定の根拠。</summary>
    private async Task<DamageCommandResult> DetailHistoryAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, CancellationToken ct)
    {
        if (args.Count == 0 || !int.TryParse(args[0].TrimStart('#'), out var id)) return Fail($"書式: !{ds.CommandPrefix} detail -h 履歴ID");
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        var match = roster.History.FirstOrDefault(m => m.Id == id);
        if (match is null) return Fail($"履歴ID {id} はありません");
        return Ok($"履歴 #{match.Id} {DateText(match.Date)}{MemoText(match.Memo)}: " + string.Join(" ｜ ", match.Foes.Select(f => DescribeRival(f, data, ds.EvSystem, withNotes: true))));
    }

    /// <summary>rm -h 履歴ID: 履歴の削除。</summary>
    private async Task<DamageCommandResult> RemoveHistoryAsync(DataSetInfo ds, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("履歴の削除は配信者・モデレーターのみ行えます");
        if (args.Count == 0 || !int.TryParse(args[0].TrimStart('#'), out var id)) return Fail($"書式: !{ds.CommandPrefix} rm -h 履歴ID");
        var removed = await _roster.ChangeRivalsAsync(ds.Key, roster => { var n = roster.History.RemoveAll(m => m.Id == id); return (n > 0, n > 0); }, ct).ConfigureAwait(false);
        return removed ? Ok($"履歴 #{id} を削除しました") : Fail($"履歴ID {id} はありません");
    }

    private static string DateText(string iso) => DateTimeOffset.TryParse(iso, out var d) ? d.ToLocalTime().ToString("M/d HH:mm") : iso;
    private static string MemoText(string memo) => memo.Length > 0 ? $" 「{memo}」" : "";

    // ------------------------------------------------------------------ 自分のポケモンの更新

    /// <summary>set -m 登録ID または名前 [振り方] [性格] [特性] : 登録済みの自分のポケモンの、指定した項目だけ更新。</summary>
    private async Task<DamageCommandResult> SetMyAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        if (!options.AllowMutations) return Fail("更新は配信者・モデレーターのみ行えます");
        if (args.Count < 2)
            return Fail($"書式: !{ds.CommandPrefix} set -m 登録IDか名前 [H32 A10 …] [性格 / B↑A↓] [特性]");
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        // 登録ID（#1 / 1）のほか、名前でも指定できる（同じポケモンが複数登録されていれば、登録IDを案内する）
        if (!int.TryParse(args[0].TrimStart('#'), out var id))
        {
            var match = DamageCommand.CreatePokemonResolver(data).Resolve(args[0]);
            if (!match.IsResolved) return Fail($"ポケモン「{args[0]}」が見つかりません");
            var same = roster.Pokemon.Where(e => e.PokemonId == match.Value!.Id).ToList();
            if (same.Count == 0) return Fail($"{match.Value!.Name}は登録されていません");
            if (same.Count > 1)
                return Fail($"{match.Value!.Name}は複数登録されています（{string.Join("・", same.Select(e => $"#{e.Id}"))}）。登録IDで指定してください（例: !{ds.CommandPrefix} set -m {same[0].Id} …）");
            id = same[0].Id;
        }
        var entry = roster.Pokemon.FirstOrDefault(e => e.Id == id);
        if (entry is null) return Fail($"登録ID {id} はありません");
        var pokemon = data.FindPokemon(entry.PokemonId);
        if (pokemon is null) return Fail($"#{id} {entry.PokemonName} は現在のデータに存在しません");

        // 登録のポケモン名を先頭に付けて、同じ読み方で振り方・性格・特性を読む
        var (specs, error) = ParseSpecs(new[] { pokemon.Name }.Concat(args.Skip(1)).ToList(), data, ds.EvSystem);
        if (error is not null) return Fail(error);
        var spec = specs.FirstOrDefault(s => s.Pokemon.Id == pokemon.Id);
        if (spec is null || !spec.HasInfo || specs.Count > 1) return Fail("更新する内容（振り方・性格・特性）を書いてください");

        var evs = entry.EVs;
        foreach (var (stat, value) in spec.Evs) evs[stat] = value;
        if (EvRules.Validate(ds.EvSystem, evs) is { } evError) return Fail(evError);
        var nature = spec.Nature ?? data.FindNature(entry.Nature) ?? NatureSelection.Neutral(data.Natures)!;
        var ability = spec.Ability ?? (entry.AbilityId is { } aid ? data.FindAbility(aid) : null);
        var updated = await _roster.UpdatePokemonAsync(ds.Key, id, evs, nature, ability, entry.MoveIds, ct).ConfigureAwait(false);
        if (updated is null) return Fail($"登録ID {id} はありません");
        var build = RosterRepository.ToBuild(updated, data)!;
        return Ok($"#{updated.Id} {pokemon.Name} を更新しました: {Describe(updated, build)}");
    }

    // ------------------------------------------------------------------ 表示

    public static string DescribeRival(RivalPokemon r, PokemonDataSet data, EvSystem system, bool withName = true, bool withNotes = false)
    {
        var unit = system == EvSystem.Points ? "pt" : "";
        var parts = new List<string>();
        var exact = new List<string>();
        if (r.HP is { } h) exact.Add($"H{h}");
        if (r.Attack is { } a) exact.Add($"A{a}");
        if (r.Defense is { } b) exact.Add($"B{b}");
        if (r.SpAttack is { } c) exact.Add($"C{c}");
        if (r.SpDefense is { } d) exact.Add($"D{d}");
        if (r.Speed is { } s) exact.Add($"S{s}");
        if (exact.Count > 0) parts.Add(string.Join("", exact));
        if (r.Nature is { } nature) parts.Add(data.FindNature(nature)?.Summary() ?? nature);
        if (r.AbilityId is { } aid && data.FindAbility(aid) is { } ability) parts.Add(ability.Name);
        foreach (var scan in r.Scans)
        {
            var letter = scan.Defense == nameof(Stat.Defense) ? "B" : "D";
            var bands = string.Join("/", scan.Bands.OrderByDescending(x => x.Modifier).Select(x =>
                $"{(x.Modifier > 1 ? "上昇" : x.Modifier < 1 ? "下降" : "補正なし")}{(x.MinEv == x.MaxEv ? x.MinEv.ToString() : $"{x.MinEv}〜{x.MaxEv}")}{unit}"));
            var hp = scan.HpMin is { } lo && scan.HpMax is { } hi ? $" H{(lo == hi ? lo.ToString() : $"{lo}〜{hi}")}{unit}" : "";
            parts.Add($"推定{letter}:{bands}{hp}({scan.Count}回)");
        }
        if (parts.Count == 0) parts.Add("名前だけ");
        var text = (withName ? r.PokemonName : "") + $"({string.Join(" ", parts)})";
        if (withNotes && r.Scans.Count > 0)
            text += " 根拠: " + string.Join("、", r.Scans.SelectMany(x => x.Notes));
        return text;
    }

    // ------------------------------------------------------------------ 推定（scan）

    /// <summary>
    /// scan 攻撃側 技 相手 受けたダメージ [オプション]: 受けたダメージから相手の防御の振り方を推定し、相手ポケモンに反映する
    /// （前の推定があれば重なる範囲に狭める。登録していなければ自動で登録）。
    /// </summary>
    private async Task<DamageCommandResult> ScanAsync(DataSetInfo ds, PokemonDataSet data, List<string> args, PokeCommandOptions options, CancellationToken ct)
    {
        // scan は相手ポケモンの登録・絞り込みを保存するので、変更系と同じく配信者・モデレーターだけ
        if (!options.AllowMutations) return Fail("scan は相手ポケモンを登録するため、配信者・モデレーターのみ使えます");
        if (args.Count < 4) return Fail($"書式: !{ds.CommandPrefix} scan 攻撃側 技±ランク 相手 受けたダメージ（例: 93 または 45%）");
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
        var scanText = $"{args[0]} {args[1]} {args[2]} {args[damageIndex]}";
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
        var pokemon = request.Defender.Pokemon;

        // 相手の特性が分かっていればその特性で、分からなければ持ちうる特性ごとに推定する（登録の特性があればそれ）
        var roster = (await _roster.GetAsync(ct).ConfigureAwait(false)).For(ds.Key);
        var existing = roster.Rivals.FirstOrDefault(r => r.PokemonId == pokemon.Id);
        var knownAbility = existing?.AbilityId is { } aid ? data.FindAbility(aid) : null;
        if (knownAbility is not null) request.Defender.Ability = knownAbility;
        var abilities = MentionsAbility(args, data, pokemon) || request.Defender.Ability is null
            ? new List<Ability?> { request.Defender.Ability }
            : data.AbilitiesOf(pokemon).Cast<Ability?>().ToList();
        var results = new List<(Ability? Ability, EvEstimator.Estimate Estimate)>();
        foreach (var ability in abilities)
        {
            var defender = request.Defender.Clone();
            defender.Ability = ability;
            results.Add((ability, estimator.Run(request.With(defender: defender), observed, asPercent)));
        }
        var found = results.Where(r => r.Estimate.HasResult).ToList();
        var observedText = asPercent ? $"{observed:0.#}%" : $"{observed:0}";
        if (found.Count == 0)
            return new DamageCommandResult { Success = false, Message = $"{attackerText}の{request.Move.Name}で{pokemon.Name}に{observedText}ダメージになる振り方が見つかりません。持ち物・特性・ランク・天候・フィールドが違うか、ダメージを見直してください（相手は持ち物なし想定）", Request = request };

        // 特性ごとの結果は、重なる範囲ではなく「どれかの特性なら成り立つ範囲」（和）にまとめて反映する
        var merged = MergeEstimates(found.Select(r => r.Estimate).ToList());
        var note = $"{scanText}";
        var now = DateTimeOffset.Now;
        (bool Applied, string? Problem) applied = (false, null);
        var rivalText = "";
        var rivalAdded = false;
        string? tooMany = null;
        await _roster.ChangeRivalsAsync(ds.Key, r =>
        {
            tooMany = null;
            var rival = r.Rivals.FirstOrDefault(x => x.PokemonId == pokemon.Id);
            if (rival is null)
            {
                if (r.Rivals.Count >= MaxRivals) { tooMany = $"対戦中の相手が {MaxRivals} 体いるため登録できません（clear -r で試合を終えてください）"; return (0, false); }
                rival = new RivalPokemon { Id = r.NextRivalId++, PokemonId = pokemon.Id, PokemonName = pokemon.Name };
                r.Rivals.Add(rival);
                rivalAdded = true;
            }
            else rivalAdded = false;
            var working = rival.Clone();
            applied = RivalScanning.Apply(working, merged, note, now);
            if (!applied.Applied) return (0, rivalAdded);   // 矛盾したときは、前の推定のまま（新しく登録した場合は名前だけで残す）
            r.Rivals[r.Rivals.IndexOf(rival)] = working;
            rivalText = DescribeRival(working, data, ds.EvSystem);
            return (0, true);
        }, ct).ConfigureAwait(false);

        // 推定の内訳（特性による差があれば特性別に）
        string Bands(EvEstimator.Estimate e)
        {
            var unit = ds.EvSystem == EvSystem.Points ? "pt" : "";
            var letter = StatNames.Letter(e.DefenseStat);
            var text = string.Join(" / ", e.Bands.Select(b => b.MinEv == b.MaxEv ? $"{b.Label}:{letter}{b.MinEv}{unit}" : $"{b.Label}:{letter}{b.MinEv}〜{b.MaxEv}{unit}"));
            return e.HpEvRange is { } h ? text + $" H{h.MinEv}〜{h.MaxEv}{unit}" : text;
        }
        var head = results.Count == 1 || results.Select(r => Bands(r.Estimate)).Distinct().Count() == 1
            ? $"{attackerText}の{EvEstimator.Format(found[0].Estimate, pokemon, request.Move, ds.EvSystem)}"
            : $"{attackerText}の{request.Move.Name}で{pokemon.Name}に{observedText}ダメージ → 特性別: " + string.Join(" ｜ ", results.Select(r => $"{r.Ability?.Name}: {(r.Estimate.HasResult ? Bands(r.Estimate) : "該当なし")}"));
        if (tooMany is not null) return new DamageCommandResult { Success = false, Message = $"{head} ※{tooMany}", Request = request };
        if (!applied.Applied) return new DamageCommandResult { Success = false, Message = $"{head} ※{applied.Problem}", Request = request };
        return new DamageCommandResult
        {
            Success = true,
            Message = $"{head} ※相手は持ち物なし想定。{pokemon.Name}を{(rivalAdded ? "相手に登録" : "絞り込み")}: {rivalText}",
            Request = request,
        };
    }

    /// <summary>特性ごとの推定を、性格補正ごとの範囲の和（最小〜最大）にまとめる。1 つだけならそのまま。</summary>
    private static EvEstimator.Estimate MergeEstimates(IReadOnlyList<EvEstimator.Estimate> estimates)
    {
        if (estimates.Count == 1) return estimates[0];
        var first = estimates[0];
        var bands = estimates.SelectMany(e => e.Bands).GroupBy(b => b.Modifier).OrderByDescending(g => g.Key)
            .Select(g => new EvEstimator.NatureBand(g.Key, g.First().Label, g.Min(b => b.MinEv), g.Max(b => b.MaxEv))).ToList();
        var hps = estimates.Select(e => e.HpEvRange).ToList();
        (int, int)? hp = hps.All(h => h is not null) ? (hps.Min(h => h!.Value.MinEv), hps.Max(h => h!.Value.MaxEv)) : null;
        return new EvEstimator.Estimate
        {
            DefenseStat = first.DefenseStat,
            ObservedDamage = first.ObservedDamage,
            ObservedAsPercent = first.ObservedAsPercent,
            MinDefenseStat = estimates.Min(e => e.MinDefenseStat),
            MaxDefenseStat = estimates.Max(e => e.MaxDefenseStat),
            Bands = bands,
            HpEvRange = hp,
        };
    }
}
