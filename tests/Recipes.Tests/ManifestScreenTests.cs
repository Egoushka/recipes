using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace Recipes.Tests;

public partial class ManifestScreenTests
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SnakePattern();

    [GeneratedRegex("^(?!shell\\.)[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$")]
    private static partial Regex ActionPattern();

    private static readonly string[] OfflineModes = ["cache", "live"];

    private static async Task<JsonObject> Manifest(AppFactory app)
    {
        using var client = app.Authed();
        return JsonNode.Parse(await client.GetStringAsync("/shell/manifest"))!.AsObject();
    }

    [Fact]
    public async Task Manifest_FollowsTheSameRulesAsTheShell()
    {
        await using var app = new AppFactory();
        var m = await Manifest(app);

        Assert.Equal("shell-manifest/1", m["schema"]!.GetValue<string>());
        Assert.Matches(IdPattern(), m["id"]!.GetValue<string>());
        Assert.Equal("recipes", m["id"]!.GetValue<string>());
        Assert.InRange(m["name"]!.GetValue<string>().Length, 1, 40);
        Assert.Matches(SnakePattern(), m["icon"]!.GetValue<string>());
        Assert.NotEmpty(m["catalogs"]!.AsArray());

        var screens = m["screens"]!.AsArray();
        var ids = screens.Select(s => s!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(1, screens.Count(s => s!["home"]?.GetValue<bool>() == true));
        foreach (var s in screens)
        {
            Assert.Matches("^[a-z][a-z0-9-]*$", s!["id"]!.GetValue<string>());
            Assert.Contains(s["offline"]!.GetValue<string>(), OfflineModes);
            foreach (var p in s["params"]?.AsArray() ?? [])
                Assert.Matches(SnakePattern(), p!.GetValue<string>());
        }

        var actions = m["actions"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()).ToList();
        Assert.All(actions, a => Assert.Matches(ActionPattern(), a));
        Assert.Equal(["dial.convert", "entry.save", "entry.search"], actions.Order());
        Assert.True(m["actions"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "entry.save")!["offline"]!["queue"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Manifest_McpUrlIsHttpsAndPointsAtThisService()
    {
        await using var app = new AppFactory();
        var m = await Manifest(app);
        Assert.Equal("https://recipes.example.test/mcp", m["mcp"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Screens_EveryDeclaredScreenIsADocumentTheShellCanRender()
    {
        await using var app = new AppFactory();
        var seedId = JournalSeed(app);
        var manifest = await Manifest(app);
        using var client = app.Authed();

        foreach (var screen in manifest["screens"]!.AsArray())
        {
            var id = screen!["id"]!.GetValue<string>();
            var query = screen["params"] is JsonArray { Count: > 0 } ? $"?entry_id={seedId}" : "";
            using var response = await client.GetAsync($"/shell/screens/{id}{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

            Assert.Equal(id, doc["screen"]!.GetValue<string>());
            Assert.InRange(doc["ttl_s"]!.GetValue<int>(), 0, 86400);
            Assert.NotEmpty(doc["etag"]!.GetValue<string>());
            Assert.NotNull(response.Headers.ETag);
            CheckMessages(id, doc["messages"]!.AsArray());
        }
    }

    [Fact]
    public async Task Screens_SameContentGives304OnRevalidation()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        using var first = await client.GetAsync("/shell/screens/oven");
        var etag = first.Headers.ETag!.ToString();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/shell/screens/oven");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Screens_SavingAnEntryChangesTheHomeDocument()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        using var before = await client.GetAsync("/shell/screens/home");
        app.Store.Save(new(null, null, "recipe", "Pancakes", "", [], null, null));
        using var after = await client.GetAsync("/shell/screens/home");

        Assert.NotEqual(before.Headers.ETag, after.Headers.ETag);
        Assert.Contains("Pancakes", await after.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Screens_UnknownScreenIs404AndMissingOrUnknownParamIsRefused()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/shell/screens/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/shell/screens/entry")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/shell/screens/entry?entry_id=e_missing")).StatusCode);
    }

    [Fact]
    public async Task Screens_OpeningAnEntryCountsAsALookupButEditingDoesNot()
    {
        await using var app = new AppFactory();
        var id = JournalSeed(app);
        using var client = app.Authed();
        app.Clock.Advance(TimeSpan.FromHours(30));
        app.Store.Save(new(null, null, "recipe", "x", "", [], null, null));

        using var _ = await client.GetAsync($"/shell/screens/entry-new?entry_id={id}");
        Assert.Equal(0, app.Store.GetStats().OlderLookups);

        using var __ = await client.GetAsync($"/shell/screens/entry?entry_id={id}");
        Assert.Equal(1, app.Store.GetStats().OlderLookups);
    }

    [Fact]
    public async Task Screens_EntryShowsTheOvenMarksOfADialConfig()
    {
        await using var app = new AppFactory();
        var id = JournalSeed(app);
        using var client = app.Authed();
        var text = await client.GetStringAsync($"/shell/screens/entry?entry_id={id}");
        Assert.Contains("200 °C", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seed_OvenScreenListsTheMarks()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        var text = await client.GetStringAsync("/shell/screens/oven");
        Assert.Contains("mark 6", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("200", text, StringComparison.Ordinal);
    }

    private static string JournalSeed(AppFactory app)
    {
        _ = app.Services; // starts the host, which seeds the database
        return Recipes.Storage.JournalStore.SeedId;
    }

    private static void CheckMessages(string screen, JsonArray messages)
    {
        Assert.All(messages, m => Assert.Equal("v0.9", m!["version"]!.GetValue<string>()));
        var create = messages.Single(m => m!["createSurface"] is not null)!["createSurface"]!;
        Assert.Equal(screen, create["surfaceId"]!.GetValue<string>());
        Assert.Equal("shell-catalog/1", create["catalogId"]!.GetValue<string>());

        var components = messages.Single(m => m!["updateComponents"] is not null)!["updateComponents"]!["components"]!.AsArray();
        var ids = components.Select(c => c!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains("root", ids);

        foreach (var c in components)
        {
            foreach (var child in ChildIds(c!))
                Assert.Contains(child, ids);
        }
    }

    private static IEnumerable<string> ChildIds(JsonNode c)
    {
        if (c["children"] is JsonArray list)
        {
            foreach (var n in list)
                yield return n!.GetValue<string>();
        }
        else if (c["children"]?["componentId"] is JsonNode template)
        {
            yield return template.GetValue<string>();
        }

        if (c["child"] is JsonNode one)
            yield return one.GetValue<string>();

        foreach (var tab in c["tabs"]?.AsArray() ?? [])
            yield return tab!["child"]!.GetValue<string>();
    }
}
