using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recipes.Domain;
using Recipes.Storage;

namespace Recipes.Mcp;

/// <summary>The one JSON shape of an entry, a hit and the stats, shared by the MCP tools and the HTTP endpoints.</summary>
internal static class EntryJson
{
    /// <summary>Plain UTF-8 text instead of \u escapes: shorter for agents, readable in logs. Output is JSON, never HTML.</summary>
    public static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Text(JsonNode node) => node.ToJsonString(Options);

    public static JsonObject Of(Entry e) => new()
    {
        ["id"] = e.Id,
        ["kind"] = e.Kind,
        ["title"] = e.Title,
        ["body"] = e.Body,
        ["tags"] = new JsonArray([.. e.Tags.Select(t => (JsonNode?)JsonValue.Create(t))]),
        ["data"] = e.Data?.DeepClone(),
        ["source"] = e.Source,
        ["version"] = e.Version,
        ["created_at"] = e.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        ["updated_at"] = e.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
    };

    public static JsonObject Of(Hit h)
    {
        var o = Of(h.Entry);
        o["score"] = Math.Round(h.Score, 4);
        return o;
    }

    public static JsonObject Of(JournalStats s) => new()
    {
        ["entries"] = s.Entries,
        ["entries_saved"] = s.EntriesSaved,
        ["older_lookups"] = s.OlderLookups,
        ["window_start"] = s.WindowStart?.ToString("O", CultureInfo.InvariantCulture),
        ["window_end"] = s.WindowEnd?.ToString("O", CultureInfo.InvariantCulture),
        ["required_entries"] = JournalStore.MinEntries,
        ["required_older_lookups"] = JournalStore.MinOlderLookups,
        ["pass"] = s.Pass,
        ["status"] = s.Status,
    };

    public static JsonArray Errors(IEnumerable<FieldError> errors, string prefix = "") => new([.. errors.Select(e =>
        (JsonNode?)new JsonObject { ["path"] = prefix + e.Path, ["message"] = e.Message })]);
}
