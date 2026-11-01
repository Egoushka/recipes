using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Recipes.Search;
using Recipes.Shell;
using Recipes.Storage;

namespace Recipes.Tests;

internal sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token";
    public const string ActionKey = "test-shell-key";

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 31, 18, 40, 3, TimeSpan.Zero));

    public FakeEmbedder? Embedder { get; init; }

    public JournalStore Store => Services.GetRequiredService<JournalStore>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Journal:Path", ":memory:");
        builder.UseSetting("Recipes:Token", Token);
        builder.UseSetting("Shell:ActionKey", ActionKey);
        builder.UseSetting("Shell:PublicUrl", "https://recipes.example.test");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
            if (Embedder is not null)
            {
                s.RemoveAll<IEmbedder>();
                s.AddSingleton<IEmbedder>(Embedder);
            }
        });
    }

    public HttpClient Authed()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    public static string NewUlid()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        return new string(Enumerable.Range(0, 26).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]).ToArray());
    }

    public HttpRequestMessage SignedAction(
        string name, string screen, JsonObject context, string? actionId = null, TimeSpan? skew = null, string? key = null)
    {
        actionId ??= NewUlid();
        var at = (Clock.GetUtcNow() + (skew ?? TimeSpan.Zero)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var body = new JsonObject
        {
            ["action_id"] = actionId,
            ["name"] = name,
            ["screen"] = screen,
            ["tapped_at"] = at,
            ["at"] = at,
            ["context"] = context.DeepClone(),
            ["sig"] = Signing.Sign(key ?? ActionKey, actionId, name, screen, at, context),
        };
        return new HttpRequestMessage(HttpMethod.Post, $"/shell/actions/{name}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }
}
