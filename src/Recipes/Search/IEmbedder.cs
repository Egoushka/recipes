namespace Recipes.Search;

public interface IEmbedder
{
    string Model { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}

/// <summary>Meaning search was asked for and cannot be answered: no embedder, or it failed. Maps to 503.</summary>
public sealed class MeaningUnavailableException : Exception
{
    public MeaningUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
