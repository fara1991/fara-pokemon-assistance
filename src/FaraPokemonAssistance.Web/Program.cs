using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Roster;
using FaraPokemonAssistance.Core.Text;
using FaraPokemonAssistance.Web;
using FaraPokemonAssistance.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var baseAddress = builder.HostEnvironment.BaseAddress;
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(baseAddress) });
builder.Services.AddSingleton(sp => new DataCatalog(new HttpDataSource(sp.GetRequiredService<HttpClient>(), baseAddress + "data/")));
builder.Services.AddSingleton<IRosterStore>(sp => new BrowserRosterStore(sp.GetRequiredService<IJSRuntime>()));
builder.Services.AddSingleton(sp => new RosterRepository(sp.GetRequiredService<IRosterStore>()));
builder.Services.AddSingleton(sp => new DamageCommand(sp.GetRequiredService<DataCatalog>()));
builder.Services.AddSingleton(sp => new PokeCommand(sp.GetRequiredService<DataCatalog>(), sp.GetRequiredService<RosterRepository>()));

await builder.Build().RunAsync();
