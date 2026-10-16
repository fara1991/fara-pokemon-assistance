using System.Net;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>相手のポケモン（対戦中の相手と、試合ごとの履歴）の保存。</summary>
public class RivalRosterTests
{
    private const string Ds = "Champions";
    private static readonly string Key = RemoteRosterStore.GenerateKey();

    private static RivalPokemon Rival(int id, string name, Action<RivalPokemon>? configure = null)
    {
        var r = new RivalPokemon { Id = id, PokemonId = 445, PokemonName = name };
        configure?.Invoke(r);
        return r;
    }

    [Fact]
    public async Task Ending_a_match_moves_only_foes_with_info_to_the_history()
    {
        var repo = new RosterRepository(new MemoryRosterStore());
        await repo.ChangeRivalsAsync(Ds, roster =>
        {
            roster.Rivals.Add(Rival(roster.NextRivalId++, "名前だけ"));
            roster.Rivals.Add(Rival(roster.NextRivalId++, "ポイントだけ", r => r.SpDefense = 32));
            roster.Rivals.Add(Rival(roster.NextRivalId++, "推定した", r => r.Scans.Add(new RivalScan { Defense = "SpDefense", Count = 1 })));
            roster.Rivals.Add(Rival(roster.NextRivalId++, "特性だけ", r => r.AbilityId = 5));
            return (0, true);
        });

        var ended = await repo.EndMatchAsync(Ds, "  テスト戦  ", new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.FromHours(9)));
        // 名前だけの相手も含めて、全員が履歴に移る
        Assert.Equal(4, ended.Moved);
        Assert.NotNull(ended.Match);
        Assert.Equal("テスト戦", ended.Match!.Memo);
        Assert.Equal(new[] { "名前だけ", "ポイントだけ", "推定した", "特性だけ" }, ended.Match.Foes.Select(f => f.PokemonName));

        var roster = (await repo.GetAsync()).For(Ds);
        Assert.Empty(roster.Rivals);
        Assert.Single(roster.History);
        Assert.Equal(5, roster.NextRivalId);   // ID は使い回さない
        Assert.Equal(2, roster.NextMatchId);

        // 対戦中の相手がいなければ、履歴は作らない
        var empty = await repo.EndMatchAsync(Ds, null, DateTimeOffset.Now);
        Assert.Null(empty.Match);
        Assert.Single((await repo.GetAsync()).For(Ds).History);
    }

    [Fact]
    public async Task Rivals_and_history_are_saved_to_and_loaded_from_the_cloud()
    {
        var server = new FakeRosterServer { ExpectedKey = Key };
        var store = new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key);
        var repo = new RosterRepository(store);

        await repo.ChangeRivalsAsync(Ds, roster =>
        {
            roster.Rivals.Add(Rival(roster.NextRivalId++, "カイリュー", r =>
            {
                r.SpDefense = 32;
                r.Nature = "おだやか";
                r.Scans.Add(new RivalScan { Defense = "SpDefense", Count = 2, Bands = { new RivalBand { Modifier = 1.0, MinEv = 17, MaxEv = 32 } }, Notes = { "イエッサン(♂) ワイドフォース 40" } });
            }));
            return (0, true);
        });
        await repo.EndMatchAsync(Ds, "1 試合目", DateTimeOffset.Now);
        await repo.ChangeRivalsAsync(Ds, roster => { roster.Rivals.Add(Rival(roster.NextRivalId++, "ハバタクカミ", r => r.Speed = 32)); return (0, true); });

        // クラウドには相手のポケモンの API だけで保存される
        Assert.Equal(new[] { "/api/my/rival", "/api/my/rival", "/api/my/rival" },
            server.Requests.Where(r => r.Method == HttpMethod.Put).Select(r => r.RequestUri!.AbsolutePath));
        var saved = server.State.For(Ds);
        Assert.Equal("ハバタクカミ", saved.Rivals.Single().PokemonName);
        Assert.Equal(3, saved.NextRivalId);
        Assert.Equal("カイリュー", saved.History.Single().Foes.Single().PokemonName);
        Assert.Equal("イエッサン(♂) ワイドフォース 40", saved.History.Single().Foes.Single().Scans.Single().Notes.Single());

        // 別の読み込み（別の端末・ボット）でも同じ内容が読める
        var other = new RosterRepository(new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key));
        var loaded = (await other.GetAsync()).For(Ds);
        Assert.Equal(32, loaded.Rivals.Single().Speed);
        Assert.Equal("1 試合目", loaded.History.Single().Memo);
        Assert.Equal(1.0, loaded.History.Single().Foes.Single().Scans.Single().Bands.Single().Modifier);
    }

    [Fact]
    public async Task Old_server_without_the_rival_api_still_loads_the_rest()
    {
        var server = new FakeRosterServer { ExpectedKey = Key };
        server.RosterJson = RosterRepository.Serialize(new RosterState { DataSets = { [Ds] = new DataSetRoster { Pokemon = { new RegisteredPokemon { Id = 1, PokemonName = "ガブリアス" } }, NextId = 2 } } });
        server.Revision = 1;
        // 相手のポケモンの API が無い古いサーバー（404）
        server.Override = request => request.RequestUri!.AbsolutePath == "/api/my/rival" ? FakeRosterServer.Json(HttpStatusCode.NotFound, "{}") : null;
        var repo = new RosterRepository(new RemoteRosterStore(new HttpClient(server), "https://pokemon.example/", Key));
        var state = (await repo.GetAsync()).For(Ds);
        Assert.Equal("ガブリアス", state.Pokemon.Single().PokemonName);
        Assert.Empty(state.Rivals);
    }

    [Fact]
    public async Task Local_store_keeps_rivals_and_history_in_the_state_json()
    {
        var store = new MemoryRosterStore();
        var repo = new RosterRepository(store);
        await repo.ChangeRivalsAsync(Ds, roster => { roster.Rivals.Add(Rival(roster.NextRivalId++, "カイリュー", r => r.Defense = 10)); return (0, true); });
        var reloaded = RosterRepository.Deserialize(store.Json).For(Ds);
        Assert.Equal(10, reloaded.Rivals.Single().Defense);
        Assert.Null(reloaded.Rivals.Single().SpDefense);
    }
}
