using FaraPokemonBattleAssistant.Core;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

public class SiteLinksTests
{
    [Theory]
    [InlineData("pokemon-dev.fara-labs.com", "https://fara-labs.com/", "https://dev.fara-labs.com/")]
    [InlineData("POKEMON-DEV.fara-labs.com", "https://fara-labs.com/", "https://dev.fara-labs.com/")]
    [InlineData("pokemon.fara-labs.com", "https://fara-labs.com/", "https://fara-labs.com/")]
    [InlineData("localhost", "https://fara-labs.com/", "https://fara-labs.com/")]
    [InlineData("", "https://fara-labs.com/", "https://fara-labs.com/")]
    [InlineData("pokemon-dev.fara-labs.com", "https://github.com/fara1991/fara-bot-moderator", "https://github.com/fara1991/fara-bot-moderator")]
    [InlineData("pokemon-dev.fara-labs.com", "https://twitter.com/game_fara", "https://twitter.com/game_fara")]
    public void Localize_switches_by_host(string host, string url, string expected)
        => Assert.Equal(expected, SiteLinks.Localize(url, host));
}
