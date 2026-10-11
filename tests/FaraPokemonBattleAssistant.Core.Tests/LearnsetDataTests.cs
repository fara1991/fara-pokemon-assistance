using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>覚える技のデータ（learnsets.csv）。空の行は「全技を覚える」扱いになるので、あってはならない。</summary>
public class LearnsetDataTests
{
    public static IEnumerable<object[]> DataSetKeys() =>
        Directory.GetDirectories(TestData.DataDirectory).Select(d => new object[] { Path.GetFileName(d) });

    private static Dictionary<string, string> Learnsets(string key) =>
        File.ReadAllLines(Path.Combine(TestData.DataDirectory, key, "learnsets.csv")).Skip(1)
            .Select(l => l.Split(',', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

    [Theory]
    [MemberData(nameof(DataSetKeys))]
    public void Every_pokemon_has_a_non_empty_learnset(string key)
    {
        var pokemonIds = File.ReadAllLines(Path.Combine(TestData.DataDirectory, key, "pokemon.csv")).Skip(1)
            .Select(l => l.Split(',', 2)[0]).ToList();
        var learnsets = Learnsets(key);
        var empty = pokemonIds.Where(id => !learnsets.TryGetValue(id, out var m) || string.IsNullOrWhiteSpace(m)).ToList();
        Assert.True(empty.Count == 0, $"{key}: 技データが空のポケモン ID: {string.Join(", ", empty)}");
    }

    [Fact]
    public async Task Champions_galarian_mr_mime_does_not_learn_eruption_or_blast_burn()
    {
        // 以前は技データが空で「全技を覚える」扱いになり、ふんか・ブラストバーンまで出ていた
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var galarMrMime = data.Pokemon.Single(p => p.Id == 10168);
        var names = data.LearnableMoves(galarMrMime.Id).Select(m => m.Name).ToHashSet();
        Assert.DoesNotContain("ふんか", names);
        Assert.DoesNotContain("ブラストバーン", names);
        Assert.Contains("サイコキネシス", names);
        Assert.True(names.Count < data.Moves.Count / 2, "全技を覚える扱いになっている");
    }
}
