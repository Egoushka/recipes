using Recipes.Domain;
using Recipes.Storage;

namespace Recipes.Search;

public enum SearchMode { Hybrid, Text, Meaning }

public sealed record SearchResult(IReadOnlyList<Hit> Hits, string ModeUsed);

/// <summary>The one path every caller (MCP tool, shell action, HTTP) goes through.</summary>
public sealed class JournalService
{
    /// <summary>Depth of each ranked list before fusion. Starting value; calibrate with the K2b gold set.</summary>
    private const int Candidates = 50;

    /// <summary>The constant from the original reciprocal rank fusion paper.</summary>
    private const int RrfK = 60;

    /// <summary>Characters sent to the embedder. Starting value: below typical embedding input limits.</summary>
    private const int EmbedChars = 6000;

    private readonly JournalStore _store;
    private readonly IEmbedder? _embedder;

    public JournalService(JournalStore store, IEmbedder? embedder)
    {
        _store = store;
        _embedder = embedder;
    }

    public JournalStore Store => _store;

    public async Task<SaveResult> SaveAsync(EntryInput input, CancellationToken ct)
    {
        var result = _store.Save(input);
        if (result is { Status: SaveStatus.Saved, Entry: { } entry })
            await TryEmbedAsync(entry, ct);
        return result;
    }

    public Entry? Lookup(string id)
    {
        var entry = _store.Get(id);
        if (entry is not null)
            _store.RecordLookup(id);
        return entry;
    }

    public async Task ReindexMissingAsync(CancellationToken ct)
    {
        if (_embedder is null)
            return;
        foreach (var entry in _store.WithoutVector(_embedder.Model, Candidates))
            await TryEmbedAsync(entry, ct);
    }

    public async Task<SearchResult> SearchAsync(
        string query, string? kind, SearchMode mode, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 50);

        if (mode == SearchMode.Text)
            return new([.. _store.SearchText(query, kind, limit)], "text");

        IReadOnlyList<Hit> meaning;
        try
        {
            meaning = await MeaningAsync(query, kind, ct);
        }
        catch (MeaningUnavailableException) when (mode == SearchMode.Hybrid)
        {
            return new([.. _store.SearchText(query, kind, limit)], "text");
        }

        if (mode == SearchMode.Meaning)
            return new([.. meaning.Take(limit)], "meaning");

        var text = _store.SearchText(query, kind, Candidates);
        return new([.. Fuse(text, meaning).Take(limit)], "hybrid");
    }

    private async Task<IReadOnlyList<Hit>> MeaningAsync(string query, string? kind, CancellationToken ct)
    {
        if (_embedder is null)
            throw new MeaningUnavailableException("No embedding service is configured.");

        float[] q;
        try
        {
            q = await _embedder.EmbedAsync(query, ct);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            throw new MeaningUnavailableException("The embedding service did not answer.", e);
        }

        var ranked = _store.Vectors(_embedder.Model, kind)
            .Where(v => v.Vector.Length == q.Length)
            .Select(v => (v.Id, Score: Cosine(q, v.Vector)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(Candidates)
            .ToList();

        var entries = _store.GetMany(ranked.Select(x => x.Id)).ToDictionary(e => e.Id);
        return [.. ranked.Where(x => entries.ContainsKey(x.Id)).Select(x => new Hit(entries[x.Id], x.Score))];
    }

    private static IEnumerable<Hit> Fuse(IReadOnlyList<Hit> text, IReadOnlyList<Hit> meaning)
    {
        var scores = new Dictionary<string, (Entry Entry, double Score)>();
        foreach (var list in new[] { text, meaning })
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var hit = list[rank];
                var add = 1.0 / (RrfK + rank + 1);
                scores[hit.Entry.Id] = scores.TryGetValue(hit.Entry.Id, out var cur)
                    ? (cur.Entry, cur.Score + add)
                    : (hit.Entry, add);
            }
        }

        return scores.Values.OrderByDescending(x => x.Score).Select(x => new Hit(x.Entry, x.Score));
    }

    private async Task TryEmbedAsync(Entry entry, CancellationToken ct)
    {
        if (_embedder is null)
            return;
        try
        {
            var text = $"{entry.Title}\n{string.Join(' ', entry.Tags)}\n{entry.Body}";
            var vector = await _embedder.EmbedAsync(text.Length > EmbedChars ? text[..EmbedChars] : text, ct);
            _store.SaveVector(entry.Id, entry.Version, _embedder.Model, vector);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // The entry is saved; ReindexMissingAsync retries the vector.
        }
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
