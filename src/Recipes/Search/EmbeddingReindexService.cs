using Microsoft.Extensions.Hosting;
using Recipes.Storage;

namespace Recipes.Search;

/// <summary>On start, embeds entries whose vector is missing (the embedding service was down when they were saved).</summary>
internal sealed partial class EmbeddingReindexService(JournalService journal, JournalStore store, IEmbedder? embedder, ILogger<EmbeddingReindexService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (embedder is null)
            return;
        try
        {
            // Each round embeds a batch; stop when a round makes no progress, so a service that stays down is not hammered.
            var missing = store.WithoutVector(embedder.Model, int.MaxValue).Count;
            while (missing > 0)
            {
                await journal.ReindexMissingAsync(stoppingToken);
                var left = store.WithoutVector(embedder.Model, int.MaxValue).Count;
                if (left >= missing)
                    break;
                missing = left;
            }

            Finished(missing);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding reindex finished, {Missing} entries still without a vector.")]
    private partial void Finished(int missing);
}
