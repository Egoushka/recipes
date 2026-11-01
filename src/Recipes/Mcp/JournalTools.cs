using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Recipes.Domain;
using Recipes.Search;
using Recipes.Storage;

namespace Recipes.Mcp;

[McpServerToolType]
public sealed class JournalTools(JournalService journal)
{
    [McpServerTool(Name = "entry_search", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Find entries of the cooking journal by words and by meaning. Returns hits best first and the mode that was used: " +
                 "'hybrid' falls back to 'text' when no embedding service answers.")]
    public async Task<string> Search(
        [Description("What to look for, in plain words.")] string query,
        [Description("Only this kind: recipe, technique, creation or config.")] string? kind = null,
        [Description("hybrid (default), text or meaning. 'meaning' fails when the embedding service is down.")] string mode = "hybrid",
        [Description("How many hits, 1-50. Default 10.")] int limit = 10,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<SearchMode>(mode, ignoreCase: true, out var parsed))
            throw new McpException("mode must be hybrid, text or meaning.");
        try
        {
            var result = await journal.SearchAsync(query, kind, parsed, limit, ct);
            return new JsonObject
            {
                ["mode_used"] = result.ModeUsed,
                ["hits"] = new JsonArray([.. result.Hits.Select(h => (JsonNode?)EntryJson.Of(h))]),
            }.ToJsonString(EntryJson.Options);
        }
        catch (MeaningUnavailableException e)
        {
            throw new McpException($"Meaning search is unavailable: {e.Message}");
        }
    }

    [McpServerTool(Name = "entry_get", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read one entry with its version list. Counts as a lookup in the 14-day test (journal_stats).")]
    public string Get([Description("Entry id, e.g. e_oven-dial.")] string id)
    {
        var entry = journal.Lookup(id) ?? throw new McpException($"No entry has id '{id}'.");
        return new JsonObject
        {
            ["entry"] = EntryJson.Of(entry),
            ["versions"] = new JsonArray([.. journal.Store.Versions(id).Select(v => (JsonNode?)new JsonObject
            {
                ["version"] = v.Version,
                ["title"] = v.Title,
                ["saved_at"] = v.SavedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            })]),
        }.ToJsonString(EntryJson.Options);
    }

    [McpServerTool(Name = "entry_save", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create an entry, or add a version to an existing one (give its id and the version you read as baseVersion; " +
                 "a stale baseVersion is refused so two devices never overwrite each other). Old versions stay readable.")]
    public async Task<string> Save(
        [Description("recipe, technique, creation or config.")] string kind,
        [Description("1-200 characters.")] string title,
        [Description("Markdown, up to 20,000 characters.")] string? body = null,
        [Description("Up to 20 tags; stored lower-case.")] string[]? tags = null,
        [Description("Optional JSON object, up to 8 KB. A config with type 'dial' holds marks: {\"1\":140,...}.")] JsonObject? data = null,
        [Description("Optional https URL the entry came from.")] string? source = null,
        [Description("Id of the entry to add a version to. Leave out to create.")] string? id = null,
        [Description("The version you read. Required with id.")] long? baseVersion = null,
        CancellationToken ct = default)
    {
        var result = await journal.SaveAsync(
            new EntryInput(id, baseVersion, kind, title, body ?? "", tags ?? [], data, source), ct);
        return result.Status switch
        {
            SaveStatus.Saved => new JsonObject { ["entry"] = EntryJson.Of(result.Entry!) }.ToJsonString(EntryJson.Options),
            SaveStatus.Conflict => throw new McpException(
                $"Conflict: the entry is at version {result.CurrentVersion}, you based this on {baseVersion}. Read it again and redo the edit."),
            _ => throw new McpException("Not saved: " + string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Message}"))),
        };
    }

    [McpServerTool(Name = "dial_convert", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Turn degrees Celsius into a position on the oven dial using the newest dial config (or the one named). " +
                 "Interpolates between the two nearest marks; refuses temperatures outside the marked range.")]
    public string DialConvert(
        [Description("Temperature in °C.")] double celsius,
        [Description("Id of a dial config entry. Default: the newest.")] string? dialId = null)
    {
        var entry = journal.Store.NewestDial(dialId) ?? throw new McpException("No dial config exists.");
        var errors = new List<FieldError>();
        if (!Dial.TryParse(entry.Data!, out var dial, errors))
            throw new McpException("The dial config is invalid: " + string.Join("; ", errors.Select(e => e.Message)));
        var result = dial!.Convert(celsius);
        if (result.Error is not null)
            throw new McpException(result.Error);
        return new JsonObject
        {
            ["celsius"] = celsius,
            ["position"] = result.Position,
            ["set"] = result.Set,
            ["between"] = new JsonArray(result.LowMark, result.HighMark),
            ["measured"] = entry.Data!["measured"]?.GetValue<bool>() ?? false,
            ["dial_id"] = entry.Id,
        }.ToJsonString(EntryJson.Options);
    }

    [McpServerTool(Name = "journal_stats", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Progress of the 14-day test: entries saved, lookups of older entries, window and pass/fail.")]
    public string Stats() => EntryJson.Text(EntryJson.Of(journal.Store.GetStats()));
}
