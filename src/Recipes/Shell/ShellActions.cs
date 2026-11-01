using System.Globalization;
using System.Text.Json.Nodes;
using Recipes.Domain;
using Recipes.Mcp;
using Recipes.Search;
using Recipes.Storage;

namespace Recipes.Shell;

/// <summary>What an action answered: the HTTP status and the body the shell reads (approvals <c>responses/</c> schema).</summary>
internal sealed record ActionAnswer(int Status, JsonObject Body);

internal sealed class ShellActions(JournalService journal)
{
    public static readonly string[] Names = ["entry.save", "entry.search", "dial.convert"];

    private const int SearchLimit = 20;

    public async Task<ActionAnswer> RunAsync(string name, JsonObject context, CancellationToken ct) => name switch
    {
        "entry.save" => await SaveAsync(context, ct),
        "entry.search" => await SearchAsync(context, ct),
        "dial.convert" => Convert(context),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private async Task<ActionAnswer> SaveAsync(JsonObject ctx, CancellationToken ct)
    {
        var id = Text(ctx, "entry_id");
        id = id.Length == 0 ? null : id;
        long? version = Number(ctx, "version") is { } v and > 0 ? (long)v : null;
        var existing = id is null ? null : journal.Store.Get(id);

        // A form edit sends no data or source; keep what the stored entry has so a shell edit never drops a dial's marks.
        var data = ctx["data"] as JsonObject ?? existing?.Data;
        var source = ctx.ContainsKey("source") ? Text(ctx, "source") : existing?.Source;
        var input = new EntryInput(id, version, FirstString(ctx["kind"]), Text(ctx, "title"), Text(ctx, "body"),
            Tags(ctx["tags"]), data, string.IsNullOrEmpty(source) ? null : source);

        var result = await journal.SaveAsync(input, ct);
        return result.Status switch
        {
            SaveStatus.Saved => new(200, new JsonObject
            {
                ["ok"] = true,
                ["message"] = "Saved",
                ["messages"] = new JsonArray(),
                ["navigate"] = new JsonObject
                {
                    ["screen"] = "entry",
                    ["params"] = new JsonObject { ["entry_id"] = result.Entry!.Id },
                },
                ["invalidate"] = new JsonArray("home", "entry"),
            }),
            SaveStatus.Conflict => new(409, new JsonObject
            {
                ["ok"] = false,
                ["message"] = $"This entry changed elsewhere (now version {result.CurrentVersion}). Open it again and redo the edit.",
            }),
            _ => new(400, new JsonObject
            {
                ["ok"] = false,
                ["message"] = "Fix the highlighted fields",
                ["errors"] = EntryJson.Errors(result.Errors, "/form/"),
            }),
        };
    }

    private async Task<ActionAnswer> SearchAsync(JsonObject ctx, CancellationToken ct)
    {
        var query = Text(ctx, "query").Trim();
        IReadOnlyList<Entry> entries = query.Length == 0
            ? journal.Store.Recent(SearchLimit)
            : [.. (await journal.SearchAsync(query, null, SearchMode.Hybrid, SearchLimit, ct)).Hits.Select(h => h.Entry)];
        return Update("home", "/entries", ShellScreens.EntryRows(entries));
    }

    private ActionAnswer Convert(JsonObject ctx)
    {
        var raw = Text(ctx, "celsius").Trim().Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var celsius))
            return Refuse("Enter the temperature as a number, for example 175.");

        var entry = journal.Store.NewestDial();
        var errors = new List<FieldError>();
        if (entry?.Data is not { } data || !Dial.TryParse(data, out var dial, errors))
            return Refuse("There is no valid dial config yet.");

        var r = dial!.Convert(celsius);
        if (r.Error is not null)
            return Refuse(r.Error);

        var text = string.Create(CultureInfo.InvariantCulture,
            $"{celsius:0.##} °C sits at {r.Position:0.0} (between mark {r.LowMark} and mark {r.HighMark}). Set mark {r.Set}.");
        return Update("oven", "/result", text);
    }

    private static ActionAnswer Update(string surface, string path, JsonNode value) => new(200, new JsonObject
    {
        ["ok"] = true,
        ["messages"] = new JsonArray(ShellScreens.Message("updateDataModel",
            new JsonObject { ["surfaceId"] = surface, ["path"] = path, ["value"] = value })),
    });

    private static ActionAnswer Refuse(string message) => new(400, new JsonObject
    {
        ["ok"] = false,
        ["message"] = message,
        ["errors"] = new JsonArray(new JsonObject { ["path"] = "/form/celsius", ["message"] = message }),
    });

    private static string Text(JsonObject ctx, string key) => ctx[key] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => "",
    };

    private static double? Number(JsonObject ctx, string key) => ctx[key] switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s)
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>A choice picker sends a list; a plain field sends a string.</summary>
    private static string FirstString(JsonNode? node) => node switch
    {
        JsonArray a => a.Count > 0 && a[0] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => "",
    };

    private static List<string> Tags(JsonNode? node) => node switch
    {
        JsonArray a => [.. a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : v.ToJsonString())],
        JsonValue v when v.TryGetValue<string>(out var s) => [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries)],
        _ => [],
    };
}
