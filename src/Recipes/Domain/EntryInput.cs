using System.Text.Json.Nodes;

namespace Recipes.Domain;

/// <summary>A save request. <c>Id</c> null creates; otherwise <c>BaseVersion</c> must equal the stored version.</summary>
public sealed record EntryInput(
    string? Id,
    long? BaseVersion,
    string Kind,
    string Title,
    string Body,
    IReadOnlyList<string> Tags,
    JsonNode? Data,
    string? Source);
