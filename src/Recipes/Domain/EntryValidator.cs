using System.Text.Json.Nodes;

namespace Recipes.Domain;

public static class EntryValidator
{
    public const int MaxTitle = 200;
    public const int MaxBody = 20_000;
    public const int MaxTags = 20;
    public const int MaxTag = 40;
    public const int MaxDataBytes = 8 * 1024;

    public static readonly string[] Kinds = ["recipe", "technique", "creation", "config"];

    public static List<FieldError> Validate(EntryInput input, JsonObject? data)
    {
        var errors = new List<FieldError>();

        if (!Kinds.Contains(input.Kind, StringComparer.Ordinal))
            errors.Add(new("kind", $"Kind must be one of: {string.Join(", ", Kinds)}."));

        var title = input.Title?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitle)
            errors.Add(new("title", $"Title must be 1-{MaxTitle} characters."));

        if ((input.Body?.Length ?? 0) > MaxBody)
            errors.Add(new("body", $"Body must be at most {MaxBody} characters."));

        var tags = input.Tags ?? [];
        if (tags.Count > MaxTags)
            errors.Add(new("tags", $"At most {MaxTags} tags."));
        for (var i = 0; i < tags.Count; i++)
        {
            if (tags[i].Trim().Length > MaxTag)
                errors.Add(new($"tags.{i}", $"A tag is at most {MaxTag} characters."));
        }

        if (data is not null)
        {
            if (data.ToJsonString().Length > MaxDataBytes)
                errors.Add(new("data", $"Data must be at most {MaxDataBytes} bytes of JSON."));
            else if (data["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "dial")
                _ = Dial.TryParse(data, out _, errors);
        }

        if (input.Source is { Length: > 0 } source
            && !(Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
        {
            errors.Add(new("source", "Source must be an https URL."));
        }

        return errors;
    }

    /// <summary>Trim, lower-case, drop empties and duplicates; keep first-seen order, sorted for stability.</summary>
    public static List<string> NormalizeTags(IEnumerable<string> tags) =>
        [.. tags.Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}
