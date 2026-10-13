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
/// データ連携 API（worker/index.js）と同じ動きをするメモリ上の偽サーバー。
/// </summary>
internal sealed class FakeRosterServer : HttpMessageHandler
{
    public long Revision { get; set; }
    public string? RosterJson { get; set; }
    public string? ExpectedKey { get; set; }
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string?> Bodies { get; } = new();
    /// <summary>PUT を処理する直前に呼ぶ（他の端末の書き込みを割り込ませる）。</summary>
    public Action<FakeRosterServer>? BeforePut { get; set; }
    /// <summary>設定するとその応答を返す。</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Bodies.Add(body);
        if (Override?.Invoke(request) is { } overridden) return overridden;
        if (request.RequestUri!.AbsolutePath != "/api/roster") return Json(HttpStatusCode.NotFound, "{}");
        var auth = request.Headers.Authorization;
        if (auth?.Scheme != "Bearer" || (ExpectedKey is not null && auth.Parameter != ExpectedKey))
            return Json(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""");

        if (request.Method == HttpMethod.Get) return Current(HttpStatusCode.OK);
        if (request.Method == HttpMethod.Delete)
        {
            Revision = 0;
            RosterJson = null;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (request.Method == HttpMethod.Put)
        {
            BeforePut?.Invoke(this);
            var node = JsonNode.Parse(body!)!.AsObject();
            var baseRevision = node["baseRevision"]!.GetValue<long>();
            if (baseRevision != Revision) return Current(HttpStatusCode.Conflict);
            RosterJson = node["roster"]!.ToJsonString();
            Revision++;
            return Json(HttpStatusCode.OK, $$"""{"revision":{{Revision}}}""");
        }
        return Json(HttpStatusCode.MethodNotAllowed, "{}");
    }

    private HttpResponseMessage Current(HttpStatusCode status) =>
        Json(status, $$"""{"revision":{{Revision}},"roster":{{RosterJson ?? "null"}}}""");

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
    public async Task Load_and_save_round_trip_with_bearer_key_and_revision()
    {
        var (server, store) = NewRemote();
        Assert.Null(await store.LoadAsync());
        Assert.Equal(0, store.Revision);

        var request = server.Requests.Single();
        Assert.Equal("https://pokemon.example/api/roster", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Key, request.Headers.Authorization.Parameter);
        Assert.DoesNotContain(Key, request.RequestUri.ToString());

        await store.SaveAsync("""{"Version":1,"DataSets":{"Champions":{"NextId":1,"Pokemon":[],"Teams":[],"ActiveTeam":null}},"Name":"ガブ"}""");
        Assert.Equal(1, store.Revision);
        var put = JsonNode.Parse(server.Bodies.Last()!)!;
        Assert.Equal(0, put["baseRevision"]!.GetValue<long>());
        Assert.Equal("ガブ", put["roster"]!["Name"]!.GetValue<string>());
        Assert.Contains("ガブ", server.Bodies.Last()); // 日本語を \uXXXX にしない

        var loaded = await store.LoadAsync();
        Assert.Equal(1, store.Revision);
        Assert.Equal("ガブ", JsonNode.Parse(loaded!)!["Name"]!.GetValue<string>());
        Assert.NotNull(store.LastSyncedAt);
    }

    [Fact]
    public async Task Base_url_without_trailing_slash_works()
    {
        var server = new FakeRosterServer { ExpectedKey = Key };
        var store = new RemoteRosterStore(new HttpClient(server), "https://pokemon.example", Key);
        await store.LoadAsync();
        Assert.Equal("https://pokemon.example/api/roster", server.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task Save_with_stale_revision_throws_conflict_and_learns_current_revision()
    {
        var (server, store) = NewRemote();
        await store.LoadAsync();
        server.WriteFromElsewhere(s => s.For(Ds).NextId = 5);
        server.WriteFromElsewhere(s => s.For(Ds).NextId = 6);

        var ex = await Assert.ThrowsAsync<RosterConflictException>(() => store.SaveAsync("""{"Version":1}"""));
        Assert.Equal(2, ex.CurrentRevision);
        Assert.Equal(2, store.Revision);
        // 次の保存は新しい版を基準にする
        await store.SaveAsync("""{"Version":1}""");
        Assert.Equal(3, server.Revision);
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
        var save = await Assert.ThrowsAsync<RosterUnavailableException>(() => store.SaveAsync("{}"));
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
    public async Task Delete_clears_the_slot()
    {
        var (server, store) = NewRemote();
        await store.SaveAsync("""{"Version":1}""");
        await store.DeleteAsync();
        Assert.Equal(HttpMethod.Delete, server.Requests.Last().Method);
        Assert.Null(server.RosterJson);
        Assert.Null(store.Revision);
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
        var saved = RosterRepository.Deserialize(server.RosterJson).For(Ds);
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
        var result = await cmd.ExecuteAsync("!pokech ls pokemon");
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
        Assert.True((await cmd.ExecuteAsync("!pokech add pokemon ガブリアス 0 32 0 0 2 32 ようき さめはだ", options)).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech add team 1 1", options)).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech use team 1", options)).Success);
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
