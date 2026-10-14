using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Models;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>タイプ相性表の「抜群を取れる技」の並び: ちからもち・ヨガパワーは物理技の威力が 2 倍として並べる。</summary>
public class HugePowerMatchupTests
{
    private static readonly Move Physical = new() { Id = 9001, Name = "物理技", Type = "Fairy", Power = 85, Category = MoveCategory.Physical };
    private static readonly Move Special = new() { Id = 9002, Name = "特殊技", Type = "Fairy", Power = 120, Category = MoveCategory.Special };

    [Fact]
    public void Huge_power_doubles_physical_moves_only()
    {
        var huge = new Ability { Id = 1, Identifier = "huge-power", Name = "ちからもち" };
        var mon = new Pokemon { Name = "テスト" };
        var target = new Pokemon { Name = "相手" };
        var physical = SelfFieldEffects.For(Physical, "Fairy", mon, huge, target, null, 9);
        var special = SelfFieldEffects.For(Special, "Fairy", mon, huge, target, null, 9);
        Assert.Equal(2.0, physical.PowerFactor);
        Assert.Equal("ちからもち", physical.Field);
        Assert.Equal(1.0, special.PowerFactor);
        Assert.Null(special.Field);
        // 特性がなければ、どちらも 1 倍
        Assert.Equal(1.0, SelfFieldEffects.For(Physical, "Fairy", mon, null, target, null, 9).PowerFactor);
        // ボディプレスは防御で計算するので掛からない
        var bodyPress = new Move { Id = 776, Name = "ボディプレス", Type = "Fighting", Power = 80, Category = MoveCategory.Physical };
        Assert.Equal(1.0, SelfFieldEffects.For(bodyPress, "Fighting", mon, huge, target, null, 9).PowerFactor);
    }

    [Fact]
    public async Task Physical_move_ranks_above_a_stronger_special_move_with_huge_power()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var mawile = data.FindPokemon(303)!;     // クチート（はがね・フェアリー）
        var target = data.FindPokemon(445)!;     // ガブリアス（ドラゴン・じめん。フェアリーが抜群）
        var huge = new Ability { Id = 1, Identifier = "huge-power", Name = "ちからもち" };
        IReadOnlyList<Move> MovesOf(Pokemon _) => new[] { Special, Physical };

        var with = TypeMatchup.WithTypes(data.TypeChart, target, null, new[] { mawile }, new[] { "Steel", "Fairy" }, MovesOf, _ => huge).Single();
        Assert.Equal("物理技", with.Moves[0].Move.Name);

        var without = TypeMatchup.WithTypes(data.TypeChart, target, null, new[] { mawile }, new[] { "Steel", "Fairy" }, MovesOf, _ => null).Single();
        Assert.Equal("特殊技", without.Moves[0].Move.Name);
    }
}

