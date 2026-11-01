using System.Net;
using System.Text.Json.Nodes;
using Recipes.Shell;

namespace Recipes.Tests;

public class ShellTests
{
    private static JsonObject Context(string title = "Banana bread") => new()
    {
        ["kind"] = "recipe",
        ["title"] = title,
        ["body"] = "Mash, fold, bake.",
        ["tags"] = "baking, Dessert",
    };

    private static async Task<JsonObject> Json(HttpResponseMessage r) =>
        JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();

    [Fact]
    public void Signing_VectorsFromApprovalsVerify()
    {
        var vectors = JsonNode.Parse(File.ReadAllText("fixtures/signature-vectors.json"))!.AsArray();
        Assert.Equal(3, vectors.Count);
        foreach (var v in vectors)
        {
            var context = v!["context"];
            Assert.Equal(v["canonical"]!.GetValue<string>(), Signing.Canonical(context));
            Assert.Equal(v["h"]!.GetValue<string>(), Signing.Hash(context));
            var sig = Signing.Sign("test-shell-key", v["action_id"]!.GetValue<string>(), v["name"]!.GetValue<string>(),
                v["screen"]!.GetValue<string>(), v["at"]!.GetValue<string>(), context);
            Assert.Equal(v["sig"]!.GetValue<string>(), sig);
        }
    }

    [Fact]
    public void Signing_ChangedContextFailsVerification()
    {
        var ctx = new JsonObject { ["a"] = 1 };
        var sig = Signing.Sign("k", "id", "n", "s", "2026-10-31T18:40:03Z", ctx);
        Assert.True(Signing.Verify("k", "id", "n", "s", "2026-10-31T18:40:03Z", ctx, sig));
        Assert.False(Signing.Verify("k", "id", "n", "s", "2026-10-31T18:40:03Z", new JsonObject { ["a"] = 2 }, sig));
        Assert.False(Signing.Verify("other", "id", "n", "s", "2026-10-31T18:40:03Z", ctx, sig));
        Assert.False(Signing.Verify("k", "id", "n", "s", "2026-10-31T18:40:03Z", ctx, "not-hex"));
    }

    [Theory]
    [InlineData(9, HttpStatusCode.OK)]
    [InlineData(-9, HttpStatusCode.OK)]
    [InlineData(11, HttpStatusCode.Forbidden)]
    [InlineData(-11, HttpStatusCode.Forbidden)]
    public async Task Signing_AtMoreThanTenMinutesFromTheServerClockIsRefused(int minutes, HttpStatusCode expected)
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        using var response = await client.SendAsync(
            app.SignedAction("entry.save", "entry-new", Context(), skew: TimeSpan.FromMinutes(minutes)));
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Signing_WrongKeyAndMissingSignatureAreForbidden()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();

        using var wrong = await client.SendAsync(app.SignedAction("entry.save", "entry-new", Context(), key: "other-key"));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        using var request = app.SignedAction("entry.save", "entry-new", Context());
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
        body.Remove("sig");
        request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var missing = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
    }

    [Fact]
    public async Task Actions_MalformedBodyIs400()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        using var response = await client.PostAsync("/shell/actions/entry.save",
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Actions_SaveCreatesTheEntryAndNavigatesToIt()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();

        using var response = await client.SendAsync(app.SignedAction("entry.save", "entry-new", Context()));
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body["ok"]!.GetValue<bool>());
        var id = body["navigate"]!["params"]!["entry_id"]!.GetValue<string>();
        Assert.Equal("entry", body["navigate"]!["screen"]!.GetValue<string>());
        Assert.Contains("home", body["invalidate"]!.AsArray().Select(n => n!.GetValue<string>()));
        var saved = app.Store.Get(id)!;
        Assert.Equal(["baking", "dessert"], saved.Tags);
    }

    [Fact]
    public async Task Actions_Idempotent()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        var id = AppFactory.NewUlid();

        using var first = await client.SendAsync(app.SignedAction("entry.save", "entry-new", Context(), id));
        var firstBody = await first.Content.ReadAsStringAsync();
        using var second = await client.SendAsync(app.SignedAction("entry.save", "entry-new", Context(), id));
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.False(first.Headers.Contains("Idempotent-Replay"));
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replay").Single());
        Assert.Equal(firstBody, secondBody);
        Assert.Single(app.Store.SearchText("Banana", null, 10));
    }

    [Fact]
    public async Task Actions_InvalidFieldsAre400WithPathsAndNothingIsSaved()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();

        using var response = await client.SendAsync(app.SignedAction("entry.save", "entry-new", Context(title: "")));
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(body["ok"]!.GetValue<bool>());
        Assert.Contains(body["errors"]!.AsArray(), e => e!["path"]!.GetValue<string>() == "/form/title");
        Assert.Empty(app.Store.SearchText("Mash", null, 10));
    }

    [Fact]
    public async Task Actions_StaleVersionIs409AndKeepsTheStoredEntry()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        using var created = await client.SendAsync(app.SignedAction("entry.save", "entry-new", Context()));
        var id = (await Json(created))["navigate"]!["params"]!["entry_id"]!.GetValue<string>();

        var edit = Context();
        edit["entry_id"] = id;
        edit["version"] = 1;
        edit["body"] = "second";
        using var ok = await client.SendAsync(app.SignedAction("entry.save", "entry-new", edit));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        edit["body"] = "stale";
        using var stale = await client.SendAsync(app.SignedAction("entry.save", "entry-new", edit));
        var body = await Json(stale);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("version 2", body["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("second", app.Store.Get(id)!.Body);
    }

    [Fact]
    public async Task Actions_SearchAnswersWithAnUpdateOfTheResultsList()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        using var _ = await client.SendAsync(app.SignedAction("entry.save", "entry-new", Context()));

        using var response = await client.SendAsync(
            app.SignedAction("entry.search", "home", new JsonObject { ["query"] = "banana" }));
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var update = body["messages"]!.AsArray().Single()!["updateDataModel"]!;
        Assert.Equal("home", update["surfaceId"]!.GetValue<string>());
        Assert.Equal("/entries", update["path"]!.GetValue<string>());
        Assert.Equal("Banana bread", update["value"]![0]!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Actions_DialConvertAnswersTheMark()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();

        using var response = await client.SendAsync(
            app.SignedAction("dial.convert", "oven", new JsonObject { ["celsius"] = "175" }));
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = body["messages"]!.AsArray().Single()!["updateDataModel"]!["value"]!.GetValue<string>();
        Assert.Contains("3.5", text, StringComparison.Ordinal);
        Assert.Contains("mark 4", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Actions_UnknownActionIs404()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        using var response = await client.SendAsync(app.SignedAction("cook.save", "home", new JsonObject()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
