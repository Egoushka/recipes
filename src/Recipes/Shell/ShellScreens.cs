using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Recipes.Domain;
using Recipes.Storage;

namespace Recipes.Shell;

/// <summary>
/// Screens and manifest of the approvals shell. A screen is an A2UI v0.9 document in the shell catalog
/// (approvals <c>contract/shell/consumers/recipes/*.json</c> show the shape). Not validated against the shell's
/// schema in this repo's tests; see the spec, acceptance A9.
/// </summary>
internal static class ShellScreens
{
    public const string Version = "0.1.0";
    public const int HomeLimit = 30;

    /// <summary>How long the shell may serve a screen from its cache. Starting value, as in the contract examples.</summary>
    private const int TtlSeconds = 300;

    public static JsonObject Manifest(string publicUrl) => new()
    {
        ["schema"] = "shell-manifest/1",
        ["id"] = "recipes",
        ["name"] = "Cooking journal",
        ["icon"] = "restaurant",
        ["version"] = Version,
        ["catalogs"] = new JsonArray("https://a2ui.org/specification/v0_9/catalogs/basic/catalog.json", "shell-catalog/1"),
        ["screens"] = new JsonArray(
            Screen("home", "Journal", home: true),
            Screen("entry", "Entry", ["entry_id"]),
            Screen("entry-new", "Write an entry", ["entry_id"]),
            Screen("oven", "Oven dial")),
        ["actions"] = new JsonArray(
            Action("entry.save", queue: true),
            Action("entry.search", queue: false),
            Action("dial.convert", queue: false)),
        ["mcp"] = new JsonObject { ["url"] = publicUrl.TrimEnd('/') + "/mcp" },
    };

    private static JsonObject Screen(string id, string title, string[]? parameters = null, bool home = false)
    {
        var s = new JsonObject { ["id"] = id, ["title"] = title, ["offline"] = "cache" };
        if (home)
            s["home"] = true;
        if (parameters is not null)
            s["params"] = new JsonArray([.. parameters.Select(p => (JsonNode?)p)]);
        return s;
    }

    private static JsonObject Action(string name, bool queue) =>
        new() { ["name"] = name, ["offline"] = new JsonObject { ["queue"] = queue } };

    public static JsonObject Home(IReadOnlyList<Entry> entries) => Doc("home",
        new JsonObject { ["query"] = "", ["entries"] = EntryRows(entries) },
        [
            Comp("root", "Column", ("children", Strs("hdr", "query", "search_btn", "list", "new_btn", "oven_btn"))),
            Text("hdr", "Cooking journal", "h2"),
            Comp("query", "TextField", ("label", "Search"), ("value", Path("/query"))),
            Text("search_label", "Search"),
            Comp("search_btn", "Button", ("child", "search_label"), ("variant", "primary"),
                ("action", Event("entry.search", ("query", Path("/query"))))),
            Comp("list", "List", ("children", new JsonObject { ["componentId"] = "row", ["path"] = "/entries" })),
            Comp("row", "Card", ("child", "row_col")),
            Comp("row_col", "Column", ("children", Strs("row_title", "row_meta", "row_open_label", "row_open"))),
            Text("row_title", Path("title"), "h4"),
            Text("row_meta", Path("meta"), "caption"),
            Text("row_open_label", "Open"),
            Comp("row_open", "Button", ("child", "row_open_label"), ("variant", "borderless"),
                ("action", Navigate("entry", ("entry_id", Path("id"))))),
            Text("new_label", "New entry"),
            Comp("new_btn", "Button", ("child", "new_label"), ("action", Navigate("entry-new"))),
            Text("oven_label", "Oven dial"),
            Comp("oven_btn", "Button", ("child", "oven_label"), ("variant", "borderless"), ("action", Navigate("oven"))),
        ]);

    public static JsonObject EntryView(Entry e)
    {
        var children = new List<string> { "hdr", "meta", "body" };
        var comps = new List<JsonObject>
        {
            Text("hdr", e.Title, "h2"),
            Text("meta", Meta(e), "caption"),
            Text("body", e.Body),
        };

        if (e.Data is { } data && data["type"]?.ToString() == "dial" && Dial.TryParse(data, out var dial, []))
        {
            children.Add("marks");
            comps.Add(Text("marks", MarkLines(dial!, ", ")));
        }

        children.Add("edit_btn");
        comps.Add(Text("edit_label", "Edit"));
        comps.Add(Comp("edit_btn", "Button", ("child", "edit_label"), ("action", Navigate("entry-new", ("entry_id", e.Id)))));
        comps.Insert(0, Comp("root", "Column", ("children", Strs([.. children]))));
        return Doc("entry", null, comps);
    }

    public static JsonObject EntryForm(Entry? e)
    {
        var form = new JsonObject
        {
            ["kind"] = new JsonArray(e?.Kind ?? "recipe"),
            ["title"] = e?.Title ?? "",
            ["body"] = e?.Body ?? "",
            ["tags"] = e is null ? "" : string.Join(", ", e.Tags),
        };
        return Doc("entry-new", new JsonObject { ["form"] = form },
        [
            Comp("root", "Column", ("children", Strs("hdr", "kind", "title", "body", "tags", "save"))),
            Text("hdr", e is null ? "New entry" : $"Edit: {e.Title}", "h2"),
            Comp("kind", "ChoicePicker", ("label", "Kind"), ("variant", "mutuallyExclusive"),
                ("options", new JsonArray([.. EntryValidator.Kinds.Select(k => (JsonNode?)new JsonObject { ["label"] = k, ["value"] = k })])),
                ("value", Path("/form/kind"))),
            Comp("title", "TextField", ("label", "Title"), ("value", Path("/form/title"))),
            Comp("body", "TextField", ("label", "Body (Markdown)"), ("variant", "longText"), ("value", Path("/form/body"))),
            Comp("tags", "TextField", ("label", "Tags, comma separated"), ("value", Path("/form/tags"))),
            Text("save_label", "Save"),
            Comp("save", "Button", ("child", "save_label"), ("variant", "primary"),
                ("action", Event("entry.save", ("entry_id", e?.Id ?? ""), ("version", e?.Version ?? 0),
                    ("kind", Path("/form/kind")), ("title", Path("/form/title")),
                    ("body", Path("/form/body")), ("tags", Path("/form/tags"))))),
        ]);
    }

    public static JsonObject Oven(Entry? dialEntry)
    {
        var marks = "No dial config yet. Save a config entry with data.type = \"dial\".";
        var note = "";
        if (dialEntry?.Data is { } data && Dial.TryParse(data, out var dial, []))
        {
            marks = MarkLines(dial!, "\n");
            note = data["measured"]?.GetValue<bool>() == true
                ? "Measured with a thermometer."
                : "Generic table, not measured on this oven.";
        }

        return Doc("oven", new JsonObject { ["form"] = new JsonObject { ["celsius"] = "" }, ["result"] = "" },
        [
            Comp("root", "Column", ("children", Strs("hdr", "marks", "note", "celsius", "convert_btn", "result"))),
            Text("hdr", "Oven dial", "h2"),
            Text("marks", marks),
            Text("note", note, "caption"),
            Comp("celsius", "TextField", ("label", "Degrees °C"), ("value", Path("/form/celsius"))),
            Text("convert_label", "Which mark?"),
            Comp("convert_btn", "Button", ("child", "convert_label"), ("variant", "primary"),
                ("action", Event("dial.convert", ("celsius", Path("/form/celsius"))))),
            Text("result", Path("/result")),
        ]);
    }

    public static JsonArray EntryRows(IEnumerable<Entry> entries) => new([.. entries.Select(e =>
        (JsonNode?)new JsonObject { ["id"] = e.Id, ["title"] = e.Title, ["meta"] = Meta(e) })]);

    public static string Meta(Entry e) =>
        e.Tags.Count == 0 ? $"{e.Kind} · v{e.Version}" : $"{e.Kind} · v{e.Version} · {string.Join(", ", e.Tags)}";

    private static string MarkLines(Dial dial, string separator) =>
        string.Join(separator, dial.Marks.Select(m => string.Create(CultureInfo.InvariantCulture, $"Mark {m.Mark}: {m.Celsius} °C")));

    /// <summary>The A2UI v0.9 message triple; the etag is a hash of the messages, so equal content revalidates as 304.</summary>
    private static JsonObject Doc(string screen, JsonObject? data, IEnumerable<JsonObject> components)
    {
        var messages = new JsonArray
        {
            Message("createSurface", new JsonObject { ["surfaceId"] = screen, ["catalogId"] = "shell-catalog/1" }),
        };
        if (data is not null)
            messages.Add(Message("updateDataModel", new JsonObject { ["surfaceId"] = screen, ["path"] = "/", ["value"] = data }));
        messages.Add(Message("updateComponents", new JsonObject
        {
            ["surfaceId"] = screen,
            ["components"] = new JsonArray([.. components.Select(c => (JsonNode?)c)]),
        }));

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(messages.ToJsonString())))[..16];
        return new JsonObject { ["screen"] = screen, ["etag"] = $"W/\"{hash}\"", ["ttl_s"] = TtlSeconds, ["messages"] = messages };
    }

    public static JsonObject Message(string kind, JsonObject body) => new() { ["version"] = "v0.9", [kind] = body };

    private static JsonObject Comp(string id, string component, params (string Key, JsonNode Value)[] props)
    {
        var o = new JsonObject { ["id"] = id, ["component"] = component };
        foreach (var (key, value) in props)
            o[key] = value;
        return o;
    }

    private static JsonObject Text(string id, JsonNode text, string? variant = null) =>
        variant is null ? Comp(id, "Text", ("text", text)) : Comp(id, "Text", ("text", text), ("variant", variant));

    private static JsonArray Strs(params string[] ids) => new([.. ids.Select(i => (JsonNode?)i)]);

    private static JsonObject Path(string path) => new() { ["path"] = path };

    private static JsonObject Event(string name, params (string Key, JsonNode Value)[] context)
    {
        var ctx = new JsonObject();
        foreach (var (key, value) in context)
            ctx[key] = value;
        return new JsonObject { ["event"] = new JsonObject { ["name"] = name, ["context"] = ctx } };
    }

    private static JsonObject Navigate(string screen, params (string Key, JsonNode Value)[] context) =>
        Event("shell.navigate", [("screen", screen), .. context]);
}
