using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;
using FaraPokemonAssistance.Core.Roster;
using FaraPokemonAssistance.Core.Text;

// 使い方:
//   dotnet run --project src/FaraPokemonAssistance.Cli -- イエッサン♂ ワイドフォース メガリザードンX champions
//   dotnet run --project src/FaraPokemonAssistance.Cli -- --data https://fara1991.github.io/fara-pokemon-assistance/data/ ガブリアス じしん ハバタクカミ
// 引数が無ければ対話モード。

var dataArg = Environment.GetEnvironmentVariable("FPA_DATA");
var args2 = new List<string>(args);
var dataIndex = args2.IndexOf("--data");
if (dataIndex >= 0 && dataIndex + 1 < args2.Count)
{
    dataArg = args2[dataIndex + 1];
    args2.RemoveRange(dataIndex, 2);
}
var formatOption = BattleFormat.Singles;
if (args2.Remove("--doubles")) formatOption = BattleFormat.Doubles;

IDataSource source;
if (dataArg is not null && dataArg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
{
    var cacheDir = Path.Combine(Path.GetTempPath(), "FaraPokemonAssistance", "data-cache");
    source = new CachingDataSource(new HttpDataSource(new HttpClient(), dataArg), cacheDir, TimeSpan.FromDays(1));
}
else
{
    source = new FileDataSource(dataArg ?? FindLocalData());
}

var catalog = new DataCatalog(source);
var rosterPath = Environment.GetEnvironmentVariable("FPA_ROSTER")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FaraPokemonAssistance", "roster.json");
var command = new PokeCommand(catalog, new RosterRepository(new FileRosterStore(rosterPath)));
var options = new PokeCommandOptions { Format = formatOption };

async Task<string> RunAsync(string text)
{
    // 接頭辞が無ければ既定データセットのコマンドとして扱う
    if (await command.MatchAsync(text) is null)
    {
        var key = await catalog.ResolveDefaultKeyAsync();
        var prefix = (await catalog.GetDataSetsAsync()).First(d => d.Key == key).CommandPrefix;
        text = $"!{prefix} {text.TrimStart('!')}";
    }
    return (await command.ExecuteAsync(text, options)).Message;
}

if (args2.Count > 0)
{
    Console.WriteLine(await RunAsync(string.Join(' ', args2)));
    return 0;
}

Console.WriteLine(PokeCommand.Usage("pokech"));
Console.WriteLine($"登録データ: {rosterPath}（空行で終了）");
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line)) break;
    Console.WriteLine(await RunAsync(line));
}
return 0;

static string FindLocalData()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "src", "FaraPokemonAssistance.Web", "wwwroot", "data");
        if (Directory.Exists(candidate)) return candidate;
        candidate = Path.Combine(dir.FullName, "data");
        if (File.Exists(Path.Combine(candidate, "datasets.csv"))) return candidate;
        dir = dir.Parent;
    }
    throw new DirectoryNotFoundException("データフォルダが見つかりません。--data <フォルダ|URL> を指定してください。");
}
