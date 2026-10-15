using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>
/// データ共有 API（worker/index.js の /api/my/pokemon・/api/my/team・/api/my/access-key）と同じ動きをするメモリ上の偽サーバー。
/// </summary>
internal sealed class FakeRosterServer : HttpMessageHandler
{
    public long Revision { get; set; }
    /// <summary>保存している登録データ（RosterState の JSON。サーバーと同じく 1 つの文書）。</summary>
    public string? RosterJson { get; set; }
    public bool Deleted { get; set; }
    public string? ExpectedKey { get; set; }
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string?> Bodies { get; } = new();
    /// <summary>PUT を処理する直前に呼ぶ（他の端末の書き込みを割り込ませる）。</summary>
    public Action<FakeRosterServer>? BeforePut { get; set; }
    /// <summary>GET を処理する直前に呼ぶ。</summary>
    public Action<FakeRosterServer, string>? BeforeGet { get; set; }
    /// <summary>設定するとその応答を返す。</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Bodies.Add(body);
        if (Override?.Invoke(request) is { } overridden) return overridden;
        var path = request.RequestUri!.AbsolutePath;
        var methods = path switch
        {
            "/api/my/pokemon" or "/api/my/team" or "/api/my/rival" => new[] { "GET", "PUT" },
            "/api/my/access-key" => new[] { "DELETE" },
            _ => null,
        };
        if (methods is null) return Json(HttpStatusCode.NotFound, "{}");
        if (!methods.Contains(request.Method.Method)) return Json(HttpStatusCode.MethodNotAllowed, "{}");
        var auth = request.Headers.Authorization;
        if (auth?.Scheme != "Bearer" || (ExpectedKey is not null && auth.Parameter != ExpectedKey))
            return Json(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""");

        if (path == "/api/my/access-key")
        {
            Revision = 0;
            RosterJson = null;
            Deleted = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (Deleted) return Json(HttpStatusCode.Gone, """{"error":"key_retired"}""");
        var pokemon = path == "/api/my/pokemon";
        var rival = path == "/api/my/rival";
        if (request.Method == HttpMethod.Get)
        {
            BeforeGet?.Invoke(this, path);
            return View(HttpStatusCode.OK, pokemon, rival);
        }

        BeforePut?.Invoke(this);
        var node = JsonNode.Parse(body!)!.AsObject();
        var baseRevision = node["baseRevision"]!.GetValue<long>();
        if (baseRevision != Revision) return View(HttpStatusCode.Conflict, pokemon, rival);
        var state = RosterRepository.Deserialize(RosterJson);
        foreach (var (key, value) in node["dataSets"]!.AsObject())
        {
            var roster = state.For(key);
            if (rival)
            {
                roster.Rivals = value!["rivals"].Deserialize<List<RivalPokemon>>()!;
                roster.History = value["history"].Deserialize<List<RivalMatch>>()!;
                roster.NextRivalId = Math.Max(roster.NextRivalId, Math.Max(value["nextId"]?.GetValue<int>() ?? 1, roster.Rivals.Select(r => r.Id).DefaultIfEmpty(0).Max() + 1));
                roster.NextMatchId = Math.Max(roster.NextMatchId, Math.Max(value["nextMatchId"]?.GetValue<int>() ?? 1, roster.History.Select(h => h.Id).DefaultIfEmpty(0).Max() + 1));
            }
            else if (pokemon)
            {
                roster.Pokemon = value!["pokemon"].Deserialize<List<RegisteredPokemon>>()!;
                var nextId = value["nextId"]?.GetValue<int>() ?? 1;
                roster.NextId = Math.Max(roster.NextId, Math.Max(nextId, roster.Pokemon.Select(p => p.Id).DefaultIfEmpty(0).Max() + 1));
                var ids = roster.Pokemon.Select(p => p.Id).ToHashSet();
                foreach (var team in roster.Teams) team.Slots.RemoveAll(slot => !ids.Contains(slot.EntryId));
                RosterRepository.RemoveEmptyTeams(roster);
            }
            else
            {
                var teams = value!["teams"].Deserialize<List<Team>>()!;
                var active = value["activeTeam"]?.GetValue<int>();
                var ids = roster.Pokemon.Select(p => p.Id).ToHashSet();
                if (teams.Any(t => t.Slots.Count is 0 or > 6 || t.Slots.Any(slot => !ids.Contains(slot.EntryId)))
                    || (active is { } n && teams.All(t => t.Number != n)))
                    return Json(HttpStatusCode.BadRequest, """{"error":"unknown_pokemon","message":"チーム1に、登録に無いポケモン（Id 99）が入っています"}""");
                roster.Teams = teams.OrderBy(t => t.Number).ToList();
                roster.ActiveTeam = active;
            }
        }
        RosterJson = JsonNode.Parse(RosterRepository.Serialize(state))!.ToJsonString();
        Revision++;
        return Json(HttpStatusCode.OK, $$"""{"revision":{{Revision}}}""");
    }

    private HttpResponseMessage View(HttpStatusCode status, bool pokemon, bool rival = false)
    {
        var dataSets = new JsonObject();
        foreach (var (key, roster) in RosterRepository.Deserialize(RosterJson).DataSets)
        {
            dataSets[key] = rival
                ? new JsonObject { ["nextId"] = roster.NextRivalId, ["rivals"] = JsonSerializer.SerializeToNode(roster.Rivals), ["nextMatchId"] = roster.NextMatchId, ["history"] = JsonSerializer.SerializeToNode(roster.History) }
                : pokemon
                ? new JsonObject { ["nextId"] = roster.NextId, ["pokemon"] = JsonSerializer.SerializeToNode(roster.Pokemon) }
                : new JsonObject { ["teams"] = JsonSerializer.SerializeToNode(roster.Teams), ["activeTeam"] = roster.ActiveTeam };
        }
        return Json(status, new JsonObject { ["revision"] = Revision, ["dataSets"] = dataSets }.ToJsonString());
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>他の端末が書き込んだことにする（revision を進める）。</summary>
    public void WriteFromElsewhere(Action<RosterState> change)
    {
        var state = RosterRepository.Deserialize(RosterJson);
        change(state);
        RosterJson = JsonNode.Parse(RosterRepository.Serialize(state))!.ToJsonString();
        Revision++;
    }

    /// <summary>保存されている登録データ。</summary>
    public RosterState State => RosterRepository.Deserialize(RosterJson);
}

public class RosterSyncTests
{
    private const string Ds = "Champions";
    private static readonly string Key = RemoteRosterStore.GenerateKey();

    private static (FakeRosterServer Server, RemoteRosterStore Store) NewRemote(FakeRosterServer? server = null)
    {
        server ??= new FakeRosterServer { ExpectedKey = Key };
        var store = new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key);
        return (server, store);
    }

    private static async Task<(Pokemon Pokemon, Nature Nature)> SamplePokemonAsync()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        return (data.Pokemon.First(p => p.Name == "ガブリアス"), data.FindNature("ようき")!);
    }

    [Fact]
    public void Generated_keys_are_43_char_base64url_and_unique()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => RemoteRosterStore.GenerateKey()).ToList();
        Assert.All(keys, k => Assert.Matches("^[A-Za-z0-9_-]{43}$", k));
        Assert.All(keys, k => Assert.True(RemoteRosterStore.IsValidKey(k)));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 44 文字
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")] // パディング
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    public void Invalid_keys_are_rejected_without_echoing_the_key(string? key)
    {
        Assert.False(RemoteRosterStore.IsValidKey(key));
        var ex = Assert.Throws<ArgumentException>(() => new RemoteRosterStore(new HttpClient(), RemoteRosterStore.ProductionBaseUrl, key!));
        if (!string.IsNullOrEmpty(key)) Assert.DoesNotContain(key, ex.Message);
    }

    [Fact]
    public async Task Load_reads_both_resources_with_bearer_key()
    {
        var (server, store) = NewRemote();
        Assert.Null(await store.LoadAsync());
        Assert.Equal(0, store.Revision);

        Assert.Equal(new[] { "https://pokemon.example/api/my/pokemon", "https://pokemon.example/api/my/team", "https://pokemon.example/api/my/rival" },
            server.Requests.Select(r => r.RequestUri!.ToString()));
        Assert.All(server.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Key, request.Headers.Authorization.Parameter);
            Assert.DoesNotContain(Key, request.RequestUri!.ToString());
        });
    }

    [Fact]
    public async Task Save_whole_state_puts_pokemon_then_teams_and_load_combines_them()
    {
        var (server, store) = NewRemote();
        var state = new RosterState();
        var r = state.For(Ds);
        r.Pokemon.Add(new RegisteredPokemon { Id = 1, PokemonId = 445, PokemonName = "ガブリアス", Nature = "ようき" });
        r.Pokemon.Add(new RegisteredPokemon { Id = 3, PokemonId = 6, PokemonName = "リザードン" });
        r.NextId = 5;
        r.Teams.Add(new Team { Number = 2, Slots = { new TeamSlot { EntryId = 3, ItemId = 275 }, new TeamSlot { EntryId = 1 } } });
        r.ActiveTeam = 2;
        state.For("Gen9").Pokemon.Add(new RegisteredPokemon { Id = 1, PokemonId = 987, PokemonName = "ハバタクカミ" });
        state.For("Gen9").NextId = 2;

        await store.SaveAsync(RosterRepository.Serialize(state));
        Assert.Equal(2, store.Revision);
        var puts = server.Requests.Select((req, i) => (req, body: server.Bodies[i])).Where(x => x.req.Method == HttpMethod.Put).ToList();
        Assert.Equal(new[] { "/api/my/pokemon", "/api/my/team" }, puts.Select(x => x.req.RequestUri!.AbsolutePath));
        var first = JsonNode.Parse(puts[0].body!)!;
        Assert.Equal(0, first["baseRevision"]!.GetValue<long>());
        Assert.Equal(5, first["dataSets"]![Ds]!["nextId"]!.GetValue<int>());
        Assert.Equal("ガブリアス", first["dataSets"]![Ds]!["pokemon"]![0]!["PokemonName"]!.GetValue<string>());
        Assert.Null(first["dataSets"]![Ds]!["teams"]);
        Assert.Contains("ガブリアス", puts[0].body); // 日本語を \uXXXX にしない
        var second = JsonNode.Parse(puts[1].body!)!;
        Assert.Equal(1, second["baseRevision"]!.GetValue<long>());
        Assert.Equal(2, second["dataSets"]![Ds]!["activeTeam"]!.GetValue<int>());
        Assert.Null(second["dataSets"]![Ds]!["pokemon"]);

        var loaded = RosterRepository.Deserialize(await store.LoadAsync());
        Assert.Equal(2, store.Revision);
        var ch = loaded.For(Ds);
        Assert.Equal(new[] { 1, 3 }, ch.Pokemon.Select(p => p.Id));
        Assert.Equal(5, ch.NextId);
        Assert.Equal(2, ch.ActiveTeam);
        Assert.Equal(new int?[] { 275, null }, ch.Teams.Single().Slots.Select(slot => slot.ItemId));
        Assert.Equal("ハバタクカミ", loaded.For("Gen9").Pokemon.Single().PokemonName);
        Assert.NotNull(store.LastSyncedAt);
    }

    [Fact]
    public async Task Base_url_without_trailing_slash_works()
    {
        var server = new FakeRosterServer { ExpectedKey = Key };
        var store = new RemoteRosterStore(new HttpClient(server), "https://pokemon.example", Key);
        await store.LoadAsync();
        Assert.Equal("https://pokemon.example/api/my/pokemon", server.Requests.First().RequestUri!.ToString());
    }

    [Fact]
    public async Task Load_reads_again_when_revisions_differ_between_the_two_gets()
    {
        var (server, store) = NewRemote();
        var interrupted = false;
        server.BeforeGet = (s, path) =>
        {
            // 1 回目の my-pokemon と my-team の間に、他の端末が書く
            if (path != "/api/my/team" || interrupted) return;
            interrupted = true;
            s.WriteFromElsewhere(state => state.For(Ds).Pokemon.Add(new RegisteredPokemon { Id = 1, PokemonName = "他の端末" }));
        };
        var loaded = RosterRepository.Deserialize(await store.LoadAsync());
        Assert.Equal(6, server.Requests.Count);   // 1 回目（食い違い）3 つ + 読み直し 3 つ
        Assert.Equal(1, store.Revision);
        Assert.Equal("他の端末", loaded.For(Ds).Pokemon.Single().PokemonName);

        // 毎回食い違うなら諦める
        server.BeforeGet = (s, path) => { if (path == "/api/my/team") s.WriteFromElsewhere(_ => { }); };
        var ex = await Assert.ThrowsAsync<RosterUnavailableException>(() => store.LoadAsync());
        Assert.Equal(RosterUnavailableReason.Server, ex.Reason);
    }

    [Fact]
    public async Task Save_with_stale_revision_throws_conflict_and_learns_current_revision()
    {
        var (server, store) = NewRemote();
        await store.LoadAsync();
        server.WriteFromElsewhere(s => s.For(Ds).NextId = 5);
        server.WriteFromElsewhere(s => s.For(Ds).NextId = 6);

        var state = new RosterState();
        var ex = await Assert.ThrowsAsync<RosterConflictException>(() => store.SavePartAsync(state, Ds, RosterPart.Pokemon));
        Assert.Equal(2, ex.CurrentRevision);
        Assert.Equal(2, store.Revision);
        // 次の保存は新しい版を基準にする（チーム側の 409 も同じ）
        await store.SavePartAsync(state, Ds, RosterPart.Teams);
        Assert.Equal(3, server.Revision);
        server.WriteFromElsewhere(_ => { });
        Assert.Equal(4, (await Assert.ThrowsAsync<RosterConflictException>(() => store.SavePartAsync(state, Ds, RosterPart.Teams))).CurrentRevision);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RosterUnavailableReason.Unauthorized)]
    [InlineData(HttpStatusCode.Gone, RosterUnavailableReason.KeyRetired)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, RosterUnavailableReason.TooLarge)]
    [InlineData(HttpStatusCode.BadRequest, RosterUnavailableReason.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, RosterUnavailableReason.Server)]
    [InlineData(HttpStatusCode.BadGateway, RosterUnavailableReason.Server)]
    [InlineData(HttpStatusCode.Forbidden, RosterUnavailableReason.InvalidResponse)]
    public async Task Http_errors_map_to_unavailable(HttpStatusCode status, RosterUnavailableReason reason)
    {
        var (server, store) = NewRemote();
        server.Override = _ => FakeRosterServer.Json(status, """{"error":"x"}""");
        var load = await Assert.ThrowsAsync<RosterUnavailableException>(() => store.LoadAsync());
        Assert.Equal(reason, load.Reason);
        Assert.Equal((int)status, load.StatusCode);
        Assert.DoesNotContain(Key, load.Message);
        var save = await Assert.ThrowsAsync<RosterUnavailableException>(() => store.SavePartAsync(new RosterState(), Ds, RosterPart.Pokemon));
        Assert.Equal(reason, save.Reason);
    }

    [Fact]
    public async Task Wrong_key_is_reported_as_unauthorized()
    {
        var server = new FakeRosterServer { ExpectedKey = RemoteRosterStore.GenerateKey() };
        var (_, store) = NewRemote(server);
        var ex = await Assert.ThrowsAsync<RosterUnavailableException>(() => store.LoadAsync());
        Assert.Equal(RosterUnavailableReason.Unauthorized, ex.Reason);
        Assert.Contains("アクセスキーが正しくありません", ex.Message);
    }

    [Fact]
    public async Task Network_failure_and_html_responses_map_to_unavailable()
    {
        var (server, store) = NewRemote();
        server.Override = _ => throw new HttpRequestException("connection refused");
        Assert.Equal(RosterUnavailableReason.Network, (await Assert.ThrowsAsync<RosterUnavailableException>(() => store.LoadAsync())).Reason);

        // Cloudflare Access のログイン画面など（HTML）
        server.Override = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html></html>", Encoding.UTF8, "text/html") };
        Assert.Equal(RosterUnavailableReason.InvalidResponse, (await Assert.ThrowsAsync<RosterUnavailableException>(() => store.LoadAsync())).Reason);

        server.Override = _ => FakeRosterServer.Json(HttpStatusCode.OK, """{"revision":"x"}""");
        Assert.Equal(RosterUnavailableReason.InvalidResponse, (await Assert.ThrowsAsync<RosterUnavailableException>(() => store.LoadAsync())).Reason);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped()
    {
        var (server, store) = NewRemote();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cts.Token));
    }

    [Fact]
    public async Task Rejected_save_shows_the_server_reason()
    {
        var (server, store) = NewRemote();
        server.Override = r => r.Method == HttpMethod.Put
            ? FakeRosterServer.Json(HttpStatusCode.BadRequest, """{"error":"team_too_large","message":"Champions のチーム1が 7 体います（6 体まで）"}""")
            : null;
        var ex = await Assert.ThrowsAsync<RosterUnavailableException>(() => store.SavePartAsync(new RosterState(), Ds, RosterPart.Teams));
        Assert.Equal(RosterUnavailableReason.Rejected, ex.Reason);
        Assert.Contains("7 体います", ex.Message);
    }

    [Fact]
    public async Task Delete_retires_the_key()
    {
        var (server, store) = NewRemote();
        var state = new RosterState();
        state.For(Ds).Pokemon.Add(new RegisteredPokemon { Id = 1, PokemonName = "ガブリアス" });
        await store.SaveAsync(RosterRepository.Serialize(state));
        await store.DeleteAsync();
        Assert.Equal(HttpMethod.Delete, server.Requests.Last().Method);
        Assert.Equal("/api/my/access-key", server.Requests.Last().RequestUri!.AbsolutePath);
        Assert.Null(server.RosterJson);
        Assert.Null(store.Revision);
        // 以後そのキーは 410
        Assert.Equal(RosterUnavailableReason.KeyRetired, (await Assert.ThrowsAsync<RosterUnavailableException>(() => store.LoadAsync())).Reason);
        Assert.Equal(RosterUnavailableReason.KeyRetired,
            (await Assert.ThrowsAsync<RosterUnavailableException>(() => store.SavePartAsync(state, Ds, RosterPart.Pokemon))).Reason);
    }

    [Fact]
    public async Task Repository_saves_pokemon_changes_to_my_pokemon_and_team_changes_to_my_teams()
    {
        var (server, store) = NewRemote();
        var (pokemon, nature) = await SamplePokemonAsync();
        var repo = new RosterRepository(store);
        var putPaths = new List<string>();
        server.BeforePut = _ => putPaths.Add(server.Requests.Last().RequestUri!.AbsolutePath);

        var a = await repo.AddPokemonAsync(Ds, pokemon, new StatSet(), nature, null);
        var b = await repo.AddPokemonAsync(Ds, pokemon, new StatSet(), nature, null);
        await repo.AddToTeamAsync(Ds, 1, a.Id, null);
        await repo.AddToTeamAsync(Ds, 1, b.Id, null);
        await repo.AddToTeamAsync(Ds, 2, b.Id, null);
        await repo.UseTeamAsync(Ds, 2);
        await repo.ClearActiveTeamAsync(Ds);
        await repo.UseTeamAsync(Ds, 2);
        Assert.Equal(new[] { "/api/my/pokemon", "/api/my/pokemon", "/api/my/team", "/api/my/team", "/api/my/team", "/api/my/team", "/api/my/team", "/api/my/team" }, putPaths);

        // ポケモンの削除は my-pokemon だけ。チームから外す・空のチームを消す・使用中の解除はサーバーが行う
        putPaths.Clear();
        Assert.True(await repo.RemovePokemonAsync(Ds, b.Id));
        Assert.Equal(new[] { "/api/my/pokemon" }, putPaths);
        var body = JsonNode.Parse(server.Bodies.Last()!)!;
        Assert.Null(body["dataSets"]![Ds]!["teams"]);
        var saved = server.State.For(Ds);
        Assert.Equal(new[] { a.Id }, saved.Teams.Single().Slots.Select(slot => slot.EntryId));
        Assert.Equal(1, saved.Teams.Single().Number);
        Assert.Null(saved.ActiveTeam);
        Assert.Equal(3, saved.NextId); // 消した ID は使い回さない

        putPaths.Clear();
        Assert.True(await repo.RemoveTeamAsync(Ds, 1));
        Assert.Equal(new[] { "/api/my/team" }, putPaths);
        Assert.Empty(server.State.For(Ds).Teams);
        Assert.Single(server.State.For(Ds).Pokemon);
    }

    [Fact]
    public async Task Team_put_drops_slots_left_over_from_the_old_api()
    {
        // 以前の API は中身を確かめずに保存していたので、登録に無いポケモンを指す枠や 0 体のチームが残っていることがある
        var (server, store) = NewRemote();
        server.WriteFromElsewhere(state =>
        {
            var r = state.For(Ds);
            r.Pokemon.Add(new RegisteredPokemon { Id = 1, PokemonName = "ガブリアス" });
            r.NextId = 3;
            r.Teams.Add(new Team { Number = 1, Slots = { new TeamSlot { EntryId = 1 }, new TeamSlot { EntryId = 2 }, new TeamSlot { EntryId = 1 } } });
            r.Teams.Add(new Team { Number = 2, Slots = { new TeamSlot { EntryId = 9 } } });
            r.Teams.Add(new Team { Number = 3 });
            r.ActiveTeam = 2;
        });
        var repo = new RosterRepository(store);
        await repo.AddToTeamAsync(Ds, 4, 1, null);
        var saved = server.State.For(Ds);
        Assert.Equal(new[] { 1, 4 }, saved.Teams.Select(t => t.Number));
        Assert.Equal(new[] { 1 }, saved.Teams[0].Slots.Select(slot => slot.EntryId));
        Assert.Null(saved.ActiveTeam);
    }

    [Fact]
    public async Task Pokemon_put_only_sends_the_changed_game()
    {
        var (server, store) = NewRemote();
        var (pokemon, nature) = await SamplePokemonAsync();
        server.WriteFromElsewhere(state => state.For("Gen9").Pokemon.Add(new RegisteredPokemon { Id = 1, PokemonName = "ハバタクカミ" }));
        await new RosterRepository(store).AddPokemonAsync(Ds, pokemon, new StatSet(), nature, null);
        var body = JsonNode.Parse(server.Bodies.Last()!)!;
        Assert.Equal(new[] { Ds }, body["dataSets"]!.AsObject().Select(p => p.Key));
        Assert.Single(server.State.For("Gen9").Pokemon);
        Assert.Single(server.State.For(Ds).Pokemon);
    }

    [Fact]
    public async Task Repository_on_remote_store_reads_fresh_data_written_elsewhere()
    {
        var (server, store) = NewRemote();
        var (pokemon, nature) = await SamplePokemonAsync();
        var web = new RosterRepository(store);
        var bot = new RosterRepository(new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key));

        var first = await web.AddPokemonAsync(Ds, pokemon, new StatSet(), nature, null);
        var second = await bot.AddPokemonAsync(Ds, pokemon, new StatSet(), nature, null);
        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id); // ボットは Web の登録を読み直してから追加する
        await bot.AddToTeamAsync(Ds, 1, 2, null);
        await bot.UseTeamAsync(Ds, 1);

        // Web 側は何もしなくても最新の内容を読む
        Assert.Equal(2, (await web.GetAsync()).For(Ds).Pokemon.Count);
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var members = await web.ActiveTeamMembersAsync(Ds, data);
        Assert.Equal(2, Assert.Single(members).Entry.Id);
        Assert.Equal(4, server.Revision);
    }

    [Fact]
    public async Task Repository_retries_when_the_cloud_was_updated_in_between()
    {
        var (server, store) = NewRemote();
        var (pokemon, nature) = await SamplePokemonAsync();
        var repo = new RosterRepository(store);
        await repo.AddPokemonAsync(Ds, pokemon, new StatSet(), nature, null);

        // 次の保存の直前に、他の端末が 1 体登録する
        var interrupted = false;
        server.BeforePut = s =>
        {
            if (interrupted) return;
            interrupted = true;
            s.WriteFromElsewhere(state =>
            {
                var r = state.For(Ds);
                r.Pokemon.Add(new RegisteredPokemon { Id = r.NextId++, PokemonId = pokemon.Id, PokemonName = "他の端末" });
            });
        };
        var added = await repo.AddPokemonAsync(Ds, pokemon, new StatSet(), nature, null);

        Assert.Equal(3, added.Id);
        var saved = server.State.For(Ds);
        Assert.Equal(new[] { 1, 2, 3 }, saved.Pokemon.Select(p => p.Id));
        Assert.Contains(saved.Pokemon, p => p.PokemonName == "他の端末");
    }

    [Fact]
    public async Task Repository_gives_up_after_repeated_conflicts()
    {
        var (server, store) = NewRemote();
        var (pokemon, nature) = await SamplePokemonAsync();
        var repo = new RosterRepository(store);
        var puts = 0;
        server.BeforePut = s => { puts++; s.WriteFromElsewhere(_ => { }); };

        await Assert.ThrowsAsync<RosterConflictException>(() => repo.AddPokemonAsync(Ds, pokemon, new StatSet(), nature, null));
        Assert.Equal(RosterRepository.MaxConflictRetries + 1, puts);
        // 保存できなかった変更は残らない
        server.BeforePut = null;
        Assert.Empty((await repo.GetAsync()).For(Ds).Pokemon);
    }

    [Fact]
    public async Task Repository_does_not_save_when_nothing_changed()
    {
        var (server, store) = NewRemote();
        var repo = new RosterRepository(store);
        Assert.False(await repo.UseTeamAsync(Ds, 1));
        Assert.False(await repo.ClearActiveTeamAsync(Ds));
        Assert.False(await repo.RemovePokemonAsync(Ds, 1));
        Assert.DoesNotContain(server.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Repository_surfaces_unavailable_errors()
    {
        var (server, store) = NewRemote();
        var (pokemon, nature) = await SamplePokemonAsync();
        var repo = new RosterRepository(store);
        server.Override = r => r.Method == HttpMethod.Put ? FakeRosterServer.Json(HttpStatusCode.ServiceUnavailable, "{}") : null;
        var ex = await Assert.ThrowsAsync<RosterUnavailableException>(() => repo.AddPokemonAsync(Ds, pokemon, new StatSet(), nature, null));
        Assert.Equal(RosterUnavailableReason.Server, ex.Reason);
        server.Override = null;
        Assert.Empty((await repo.GetAsync()).For(Ds).Pokemon);
    }

    [Fact]
    public async Task Poke_command_reports_cloud_errors_as_messages()
    {
        var server = new FakeRosterServer { ExpectedKey = RemoteRosterStore.GenerateKey() };
        var (_, store) = NewRemote(server);
        var cmd = new FaraPokemonBattleAssistant.Core.Text.PokeCommand(TestData.Catalog(), new RosterRepository(store));
        var result = await cmd.ExecuteAsync("!pokech ls -m");
        Assert.False(result.Success);
        Assert.Contains("アクセスキーが正しくありません", result.Message);
        Assert.DoesNotContain(Key, result.Message);
    }

    [Fact]
    public async Task Poke_command_works_on_remote_store()
    {
        var (server, store) = NewRemote();
        var cmd = new FaraPokemonBattleAssistant.Core.Text.PokeCommand(TestData.Catalog(), new RosterRepository(store));
        var options = new FaraPokemonBattleAssistant.Core.Text.PokeCommandOptions { AllowMutations = true };
        Assert.True((await cmd.ExecuteAsync("!pokech add -m ガブリアス 0 32 0 0 2 32 ようき さめはだ", options)).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech add -t 1 1", options)).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech use -t 1", options)).Success);
        var saved = RosterRepository.Deserialize(server.RosterJson).For(Ds);
        Assert.Equal(1, saved.ActiveTeam);
        Assert.Single(saved.Pokemon);
    }

    [Fact]
    public async Task Memory_store_keeps_caching_like_before()
    {
        var store = new MemoryRosterStore();
        Assert.False(((IRosterStore)store).AlwaysReload);
        var (pokemon, nature) = await SamplePokemonAsync();
        var repo = new RosterRepository(store);
        var state = await repo.GetAsync();
        await repo.AddPokemonAsync(Ds, pokemon, new StatSet(), nature, null);
        // 同じオブジェクトを使い回し、変更はその場で反映される
        Assert.Same(state, await repo.GetAsync());
        Assert.Single(state.For(Ds).Pokemon);
        Assert.Contains("ガブリアス", store.Json);

        // 外から書き換えても Invalidate するまでは読み直さない
        store.Json = """{"Version":1,"DataSets":{}}""";
        Assert.Single((await repo.GetAsync()).For(Ds).Pokemon);
        repo.Invalidate();
        Assert.Empty((await repo.GetAsync()).For(Ds).Pokemon);
    }

    [Fact]
    public async Task File_store_round_trips_like_before()
    {
        var dir = Path.Combine(Path.GetTempPath(), "roster-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "roster.json");
            var (pokemon, nature) = await SamplePokemonAsync();
            var repo = new RosterRepository(new FileRosterStore(path));
            var entry = await repo.AddPokemonAsync(Ds, pokemon, new StatSet(), nature, null);
            await repo.AddToTeamAsync(Ds, 1, entry.Id, null);
            await repo.UseTeamAsync(Ds, 1);

            var reopened = new RosterRepository(new FileRosterStore(path));
            var roster = (await reopened.GetAsync()).For(Ds);
            Assert.Equal(1, roster.ActiveTeam);
            Assert.Equal(entry.Id, roster.Teams.Single().Slots.Single().EntryId);
            Assert.True(await reopened.RemovePokemonAsync(Ds, entry.Id));
            Assert.Empty((await new RosterRepository(new FileRosterStore(path)).GetAsync()).For(Ds).Teams);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
