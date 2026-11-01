namespace Recipes.Domain;

/// <summary>One refused field: a dotted path such as <c>tags.0</c> and a message that says what to change.</summary>
public sealed record FieldError(string Path, string Message);
