using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>特性名が英語のまま残っていないこと（新しい特性の日本語名は tools/ability_names_ja.csv で補う）。</summary>
public class AbilityNameTests
{
    /// <summary>日本語名を確認できず英語のまま残している特性（identifier）。確認できたら tools/ability_names_ja.csv に移す。</summary>
    private static readonly HashSet<string> Allowed = new();

    private static bool HasJapanese(string s) => s.Any(c => c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿');

    [Fact]
    public void Every_ability_in_every_data_set_has_a_japanese_name()
    {
        var english = new List<string>();
        foreach (var file in Directory.GetFiles(TestData.DataDirectory, "abilities.csv", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(file).Skip(1).Where(l => l.Length > 0))
            {
                var cols = line.Split(',');
                if (!HasJapanese(cols[2]) && !Allowed.Contains(cols[1]))
                    english.Add($"{Path.GetFileName(Path.GetDirectoryName(file))}/{cols[1]}={cols[2]}");
            }
        }
        Assert.True(english.Count == 0, "日本語名が無い特性: " + string.Join(", ", english));
    }

    [Fact]
    public void Supplement_table_matches_data()
    {
        var path = Path.Combine(TestData.RepositoryRoot, "tools", "ability_names_ja.csv");
        var table = File.ReadLines(path).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')).ToDictionary(c => c[0], c => c[1]);
        foreach (var file in Directory.GetFiles(TestData.DataDirectory, "abilities.csv", SearchOption.AllDirectories))
            foreach (var cols in File.ReadLines(file).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')))
                if (table.TryGetValue(cols[1], out var ja)) Assert.Equal(ja, cols[2]);
    }
}
