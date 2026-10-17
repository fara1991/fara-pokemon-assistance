using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>ダイマックス技・Z 技に変わったあとの技名と威力（表示）が、計算に使う威力と一致する。</summary>
public class ConvertedMoveTests
{
    private static async Task<(PokemonDataSet Data, DamageCalculator Calc)> Load(string key)
    {
        var data = await TestData.Catalog().GetDataSetAsync(key);
        return (data, new DamageCalculator(data.TypeChart, data.Info.Generation));
    }

    [Fact]
    public async Task Dynamax_name_and_power_match_the_calculation()
    {
        var (data, calc) = await Load("Gen8");
        var attacker = new PokemonBuild(data.Pokemon.First(p => p.Name == "ドサイドン"), data.Info.EvSystem) { IsDynamax = true };
        var move = data.Moves.First(m => m.Name == "ウッドハンマー");
        var defender = new PokemonBuild(data.Pokemon.First(p => p.Name == "ギャラドス"), data.Info.EvSystem);
        var result = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = defender });
        Assert.NotNull(result.Converted);
        Assert.Equal("ダイソウゲン", result.Converted!.Name);
        Assert.Equal(result.BasePower, result.Converted.Power);
        Assert.Equal(DamageCalculator.MaxMovePower(move), result.Converted.Power);
        var preview = ConvertedMoves.Preview(move, attacker, false, 8);
        Assert.Equal(result.Converted, preview);
    }

    [Fact]
    public async Task Fighting_and_poison_use_the_lower_power_table()
    {
        var (data, calc) = await Load("Gen8");
        var attacker = new PokemonBuild(data.Pokemon.First(p => p.Name == "カイリキー"), data.Info.EvSystem) { IsDynamax = true };
        var move = data.Moves.First(m => m.Name == "インファイト");
        var r = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = new PokemonBuild(data.Pokemon.First(p => p.Name == "ギャラドス"), data.Info.EvSystem) });
        Assert.Equal("ダイナックル", r.Converted!.Name);
        Assert.Equal(r.BasePower, r.Converted.Power);
    }

    [Fact]
    public async Task Z_move_name_and_power_match_the_calculation()
    {
        var (data, calc) = await Load("Gen7");
        var attacker = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス"), data.Info.EvSystem);
        var move = data.Moves.First(m => m.Name == "じしん");
        var defender = new PokemonBuild(data.Pokemon.First(p => p.Name == "カビゴン"), data.Info.EvSystem);
        var r = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = defender, IsZMove = true });
        Assert.Equal("ライジングランドオーバー", r.Converted!.Name);
        Assert.Equal(r.BasePower, r.Converted.Power);
        Assert.Equal(ZMove.Power(move), r.Converted.Power);
    }

    [Fact]
    public async Task Exclusive_z_move_is_used_for_the_matching_pokemon()
    {
        var (data, calc) = await Load("Gen7");
        var snorlax = new PokemonBuild(data.Pokemon.First(p => p.Name == "カビゴン"), data.Info.EvSystem);
        var move = data.Moves.First(m => m.Name == "ギガインパクト");
        var r = calc.Calculate(new DamageRequest { Attacker = snorlax, Move = move, Defender = new PokemonBuild(data.Pokemon.First(p => p.Name == "ギャラドス"), data.Info.EvSystem), IsZMove = true });
        Assert.Equal("ほんきをだす　こうげき", r.Converted!.Name);
        Assert.Equal(210, r.BasePower);
        // 別のポケモンが同じ技を使うときは、タイプの Z 技
        var other = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス"), data.Info.EvSystem);
        var r2 = calc.Calculate(new DamageRequest { Attacker = other, Move = move, Defender = new PokemonBuild(data.Pokemon.First(p => p.Name == "ギャラドス"), data.Info.EvSystem), IsZMove = true });
        Assert.Equal("ウルトラダッシュアタック", r2.Converted!.Name);
    }

    [Fact]
    public async Task Normal_move_has_no_converted_move_and_chat_reply_names_the_converted_move()
    {
        var (data, calc) = await Load("Gen8");
        var attacker = new PokemonBuild(data.Pokemon.First(p => p.Name == "ドサイドン"), data.Info.EvSystem);
        var move = data.Moves.First(m => m.Name == "ウッドハンマー");
        var defender = new PokemonBuild(data.Pokemon.First(p => p.Name == "ギャラドス"), data.Info.EvSystem);
        Assert.Null(calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = defender }).Converted);

        var cmd = new PokeCommand(TestData.Catalog(), new RosterRepository(new MemoryRosterStore()));
        await cmd.ExecuteAsync("!pokess add -m ドサイドン 4 252 0 0 0 252 ようき");   // 攻撃側は登録済みのポケモンだけ
        var reply = await cmd.ExecuteAsync("!pokess dmg ドサイドン ウッドハンマー ギャラドス ダイマ");
        Assert.True(reply.Success, reply.Message);
        Assert.Contains("ウッドハンマー（ダイソウゲン）", reply.Message);
    }
}
