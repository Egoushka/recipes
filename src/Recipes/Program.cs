using Microsoft.Extensions.Caching.Memory;
using Recipes.Mcp;
using Recipes.Search;
using Recipes.Shell;
using Recipes.Storage;
using Switchknife.McpGuard;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
{
    var path = sp.GetRequiredService<IConfiguration>()["Journal:Path"] ?? "/data/recipes.db";
    if (path != ":memory:" && Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir)
        Directory.CreateDirectory(dir);
    var store = new JournalStore($"Data Source={path}", sp.GetRequiredService<TimeProvider>());
    store.SeedIfMissing();
    return store;
});

var embeddingsUrl = builder.Configuration["Embeddings:Url"];
if (!string.IsNullOrWhiteSpace(embeddingsUrl))
{
    builder.Services.AddHttpClient<IEmbedder, HttpEmbedder>((http, sp) =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        return new HttpEmbedder(http, embeddingsUrl, config["Embeddings:Model"] ?? "default", config["Embeddings:Key"]);
    });
}

builder.Services.AddSingleton(sp => new JournalService(sp.GetRequiredService<JournalStore>(), sp.GetService<IEmbedder>()));
builder.Services.AddHostedService(sp => new EmbeddingReindexService(
    sp.GetRequiredService<JournalService>(), sp.GetRequiredService<JournalStore>(), sp.GetService<IEmbedder>(),
    sp.GetRequiredService<ILogger<EmbeddingReindexService>>()));
builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<JournalTools>();

var app = builder.Build();
var token = app.Configuration["Recipes:Token"];

app.MapGet("/healthz", (JournalStore store) => Results.Ok(new { status = "ok", entries = store.GetStats().Entries }));
app.MapGet("/stats", (JournalStore store) => Results.Content(EntryJson.Text(EntryJson.Of(store.GetStats())), "application/json"))
    .RequireMcpGuard(token);
app.MapMcp("/mcp").RequireMcpGuard(token);
app.MapShell(token);

app.Run();

public partial class Program;
