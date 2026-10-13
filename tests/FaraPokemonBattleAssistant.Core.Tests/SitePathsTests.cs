using FaraPokemonBattleAssistant.Core;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

public class SitePathsTests
{
    [Theory]
    [InlineData("/speed", "speed-comparison")]
    [InlineData("/sync", "data-share")]
    [InlineData("/data-sync", "data-share")]
    [InlineData("/Data-Sync/?x=1", "data-share?x=1")]
    [InlineData("/pokemon", "my-pokemon")]
    [InlineData("/roster", "my-pokemon")]
    [InlineData("/my-teams", "my-team")]
    [InlineData("/My-Teams/?x=1", "my-team?x=1")]
    [InlineData("/team", "my-team")]
    [InlineData("/types", "type-chart")]
    [InlineData("/pokedex", "pokemon-list")]
    [InlineData("/glossary", "battle-terms")]
    [InlineData("/command", "chat-commands")]
    [InlineData("/about", "about-author")]
    [InlineData("speed", "speed-comparison")]
    [InlineData("/Speed/", "speed-comparison")]
    [InlineData("/speed?p1=25", "speed-comparison?p1=25")]
    [InlineData("/types?pokemon=6#top", "type-chart?pokemon=6#top")]
    [InlineData("/glossary#h", "battle-terms#h")]
    public void Legacy_urls_redirect_keeping_query_and_hash(string from, string expected)
        => Assert.Equal(expected, SitePaths.Redirect(from));

    [Theory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("/damage-calculator")]
    [InlineData("/speed-comparison")]
    [InlineData("/data-share")]
    [InlineData("/my-pokemon")]
    [InlineData("/my-team")]
    [InlineData("/type-chart")]
    [InlineData("/pokemon-list")]
    [InlineData("/battle-terms")]
    [InlineData("/chat-commands")]
    [InlineData("/about-author")]
    [InlineData("/api/my/pokemon")]
    [InlineData("/api/my/team")]
    [InlineData("/api/my/access-key")]
    [InlineData("/api/game/Gen9/pokemon.csv")]
    public void New_urls_and_other_paths_are_not_redirected(string path)
        => Assert.Null(SitePaths.Redirect(path));

    [Fact]
    public void Legacy_targets_are_new_pages_and_never_loop()
    {
        foreach (var (old, target) in SitePaths.Legacy)
        {
            Assert.DoesNotContain(target, SitePaths.Legacy.Keys);
            Assert.NotEqual(old, target);
        }
    }

    // LegacyUrlRedirect.razor の @page と対応表がずれていないか（razor は Core から参照できないのでファイルを読む）
    [Fact]
    public void LegacyUrlRedirect_pages_match_the_table()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FaraPokemonBattleAssistant.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var razor = File.ReadAllLines(Path.Combine(dir!.FullName, "src", "FaraPokemonBattleAssistant.Web", "Components", "Pages", "LegacyUrlRedirect.razor"));
        var pages = razor.Where(l => l.StartsWith("@page \"")).Select(l => l.Split('"')[1].TrimStart('/')).OrderBy(x => x).ToArray();
        Assert.Equal(SitePaths.Legacy.Keys.OrderBy(x => x).ToArray(), pages);
    }
}
