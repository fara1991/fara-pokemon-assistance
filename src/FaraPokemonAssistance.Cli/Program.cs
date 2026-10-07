using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;
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
var command = new DamageCommand(catalog);
var options = new DamageCommandOptions { Format = formatOption };

if (args2.Count > 0)
{
    var result = await command.ExecuteAsync(string.Join(' ', args2), options);
    Console.WriteLine(result.Message);
    return result.Success ? 0 : 1;
}

Console.WriteLine(DamageCommand.Usage);
Console.WriteLine("（空行で終了）");
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line)) break;
    var result = await command.ExecuteAsync(line, options);
    Console.WriteLine(result.Message);
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
