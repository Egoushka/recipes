using System.Text.Json.Nodes;
using Recipes.Domain;

namespace Recipes.Storage;

public sealed record Entry(
    string Id,
    string Kind,
    string Title,
    string Body,
    IReadOnlyList<string> Tags,
    JsonObject? Data,
    string? Source,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EntryVersion(long Version, string Title, DateTimeOffset SavedAt);

public enum SaveStatus { Saved, Invalid, Conflict, NotFound }

public sealed record SaveResult(
    SaveStatus Status,
    Entry? Entry = null,
    IReadOnlyList<FieldError>? Errors = null,
    long? CurrentVersion = null)
{
    public IReadOnlyList<FieldError> Errors { get; } = Errors ?? [];
}

public sealed record Hit(Entry Entry, double Score);

/// <summary>Progress of the 14-day test in the spec. <c>Status</c>: not_started, running, passed or failed.</summary>
public sealed record JournalStats(
    int Entries,
    int EntriesSaved,
    int OlderLookups,
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd,
    bool Pass,
    string Status);
