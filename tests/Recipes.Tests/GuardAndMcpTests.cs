using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Recipes.Tests;

public class GuardAndMcpTests
{
    [Theory]
    [InlineData("/shell/manifest")]
    [InlineData("/shell/screens/home")]
    [InlineData("/stats")]
    public async Task Guard_NoTokenAndWrongTokenAre401(string path)
    {
        await using var app = new AppFactory();
        using var none = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await none.GetAsync(path)).StatusCode);

        using var wrong = app.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("/shell/manifest")]
    [InlineData("/shell/screens/home")]
    [InlineData("/stats")]
    public async Task Guard_OriginHeaderIs403EvenWithTheRightToken(string path)
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Guard_McpAndActionsNeedTheTokenToo()
    {
        await using var app = new AppFactory();
        using var none = app.CreateClient();
        using var mcp = await none.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);

        using var action = await none.SendAsync(app.SignedAction("entry.save", "entry-new", new JsonObject()));
        Assert.Equal(HttpStatusCode.Unauthorized, action.StatusCode);
    }

    [Fact]
    public async Task Guard_HealthzNeedsNoToken()
    {
        await using var app = new AppFactory();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Mcp_Tools_ListShowsFiveWithReadOnlyHintsFourTrueAndSaveFalse()
    {
        await using var app = new AppFactory();
        var result = await Rpc(app, "tools/list", new JsonObject());
        var tools = result["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);

        Assert.Equal(["dial_convert", "entry_get", "entry_save", "entry_search", "journal_stats"], tools.Keys.Order());
        foreach (var (name, tool) in tools)
        {
            var readOnly = tool["annotations"]!["readOnlyHint"]!.GetValue<bool>();
            Assert.Equal(name != "entry_save", readOnly);
        }
    }

    [Fact]
    public async Task Mcp_Tools_SaveThenGetThenSearchThenStats()
    {
        await using var app = new AppFactory();

        var saved = await Call(app, "entry_save", new JsonObject
        {
            ["kind"] = "technique",
            ["title"] = "Folding",
            ["body"] = "Fold gently with a spatula.",
            ["tags"] = new JsonArray("baking"),
        });
        var id = saved["entry"]!["id"]!.GetValue<string>();
        Assert.Equal(1, saved["entry"]!["version"]!.GetValue<int>());

        var got = await Call(app, "entry_get", new JsonObject { ["id"] = id });
        Assert.Equal("Folding", got["entry"]!["title"]!.GetValue<string>());
        Assert.Single(got["versions"]!.AsArray());

        var found = await Call(app, "entry_search", new JsonObject { ["query"] = "spatula", ["mode"] = "text" });
        Assert.Equal("text", found["mode_used"]!.GetValue<string>());
        Assert.Equal(id, found["hits"]![0]!["id"]!.GetValue<string>());

        var stats = await Call(app, "journal_stats", new JsonObject());
        Assert.Equal(1, stats["entries_saved"]!.GetValue<int>());
        Assert.Equal("running", stats["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Mcp_Tools_InvalidSaveIsAnErrorResultWithFieldPaths()
    {
        await using var app = new AppFactory();
        var result = await Rpc(app, "tools/call", new JsonObject
        {
            ["name"] = "entry_save",
            ["arguments"] = new JsonObject { ["kind"] = "soup", ["title"] = "x" },
        });
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("kind", result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_Tools_MeaningSearchWithoutEmbedderIsAnErrorResult()
    {
        await using var app = new AppFactory();
        var result = await Rpc(app, "tools/call", new JsonObject
        {
            ["name"] = "entry_search",
            ["arguments"] = new JsonObject { ["query"] = "sweet", ["mode"] = "meaning" },
        });
        Assert.True(result["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Mcp_Tools_DialConvertUsesTheSeed()
    {
        await using var app = new AppFactory();
        var ok = await Call(app, "dial_convert", new JsonObject { ["celsius"] = 200 });
        Assert.Equal(6, ok["set"]!.GetValue<int>());
        Assert.False(ok["measured"]!.GetValue<bool>());

        var outside = await Rpc(app, "tools/call", new JsonObject
        {
            ["name"] = "dial_convert",
            ["arguments"] = new JsonObject { ["celsius"] = 300 },
        });
        Assert.True(outside["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Stats_HttpEndpointMatchesTheTool()
    {
        await using var app = new AppFactory();
        using var client = app.Authed();
        var body = JsonNode.Parse(await client.GetStringAsync("/stats"))!.AsObject();
        Assert.Equal("not_started", body["status"]!.GetValue<string>());
        Assert.Equal(10, body["required_entries"]!.GetValue<int>());
        Assert.Equal(2, body["required_older_lookups"]!.GetValue<int>());
    }

    private static async Task<JsonObject> Call(AppFactory app, string tool, JsonObject args)
    {
        var result = await Rpc(app, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args });
        Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
        return JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
    }

    private static async Task<JsonObject> Rpc(AppFactory app, string method, JsonObject parameters)
    {
        using var client = app.Authed();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters }.ToJsonString(),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        var json = text.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?[5..].Trim() ?? text;
        var rpc = JsonNode.Parse(json)!.AsObject();
        Assert.Null(rpc["error"]);
        return rpc["result"]!.AsObject();
    }
}
