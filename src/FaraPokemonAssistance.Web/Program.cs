using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Text;
using FaraPokemonAssistance.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var baseAddress = builder.HostEnvironment.BaseAddress;
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(baseAddress) });
builder.Services.AddSingleton(sp => new DataCatalog(new HttpDataSource(sp.GetRequiredService<HttpClient>(), baseAddress + "data/")));
builder.Services.AddSingleton(sp => new DamageCommand(sp.GetRequiredService<DataCatalog>()));

await builder.Build().RunAsync();
