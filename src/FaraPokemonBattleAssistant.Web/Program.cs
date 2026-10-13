using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using FaraPokemonBattleAssistant.Web;
using FaraPokemonBattleAssistant.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var baseAddress = builder.HostEnvironment.BaseAddress;
// 確認用サイトで開いているときだけ fara-labs.com へのリンクを確認用に切り替える（ホスト名は実行時に決まる）
FaraPokemonBattleAssistant.Core.SiteLinks.CurrentHost = new Uri(baseAddress).Host;
// シングルトンの DataCatalog から使うので、HttpClient もシングルトンにする
// （Scoped だと Development 環境のスコープ検証で起動時に例外になる）
builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = new Uri(baseAddress) });
builder.Services.AddSingleton(sp => new DataCatalog(new HttpDataSource(sp.GetRequiredService<HttpClient>(), baseAddress + "data/")));
// 登録データの保存先。アクセスキーがあればクラウド（同じオリジンの /api/my-pokemon・/api/my-team）、無ければ localStorage
builder.Services.AddSingleton(sp => new CloudSync(sp.GetRequiredService<IJSRuntime>(), sp.GetRequiredService<HttpClient>(), baseAddress, sp));
builder.Services.AddSingleton<IRosterStore>(sp => sp.GetRequiredService<CloudSync>());
builder.Services.AddSingleton(sp => new RosterRepository(sp.GetRequiredService<IRosterStore>()));
builder.Services.AddSingleton(sp => new DamageCommand(sp.GetRequiredService<DataCatalog>()));
builder.Services.AddSingleton(sp => new PokeCommand(sp.GetRequiredService<DataCatalog>(), sp.GetRequiredService<RosterRepository>()));
builder.Services.AddSingleton(sp => new AppState(sp.GetRequiredService<DataCatalog>(), sp.GetRequiredService<IJSRuntime>()));
builder.Services.AddSingleton<PageStateStore>();
builder.Services.AddSingleton<ToastService>();

await builder.Build().RunAsync();
