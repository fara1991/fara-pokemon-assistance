using FaraPokemonBattleApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add Blazor services
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Register custom services
builder.Services.AddScoped<IPokemonDataService, PokemonDataService>();
builder.Services.AddScoped<IMoveDataService, MoveDataService>();
builder.Services.AddScoped<IItemDataService, ItemDataService>();
builder.Services.AddScoped<ITypeEffectivenessService, TypeEffectivenessService>();
builder.Services.AddScoped<IDamageCalculationService, DamageCalculationService>();
builder.Services.AddScoped<IGenerationService, GenerationService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
