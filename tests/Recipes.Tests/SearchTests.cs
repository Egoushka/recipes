using Microsoft.Extensions.Time.Testing;
using Recipes.Domain;
using Recipes.Search;
using Recipes.Storage;

namespace Recipes.Tests;

/// <summary>Three made-up concepts stand in for a model: sweet things, savoury things, oven things.</summary>
internal sealed class FakeEmbedder : IEmbedder
{
    private static readonly string[][] Concepts =
    [
        ["banana", "cake", "sweet", "dessert", "sugar"],
        ["soup", "leek", "broth", "savoury"],
        ["oven", "dial", "degrees", "bake"],
    ];

    public bool Fail { get; set; }

    public string Model => "fake-3d";

    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        if (Fail)
            throw new HttpRequestException("embedding service is down");
        var words = text.ToLowerInvariant().Split([' ', '\n', ',', '.'], StringSplitOptions.RemoveEmptyEntries);
        return Task.FromResult(Concepts.Select(c => (float)words.Count(w => c.Contains(w))).ToArray());
    }
}

public class SearchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static (JournalService Service, JournalStore Store, FakeEmbedder? Embedder) Make(bool withEmbedder = true)
    {
        var store = new JournalStore("Data Source=:memory:", new FakeTimeProvider(T0));
        var embedder = withEmbedder ? new FakeEmbedder() : null;
        return (new JournalService(store, embedder), store, embedder);
    }

    private static EntryInput New(string title, string body, params string[] tags) =>
        new(null, null, "recipe", title, body, tags, null, null);

    private static async Task<(string Banana, string Soup)> Fill(JournalService s)
    {
        var banana = (await s.SaveAsync(New("Banana loaf", "ripe banana, sugar, walnuts"), default)).Entry!.Id;
        var soup = (await s.SaveAsync(New("Leek soup", "leek, broth, cream"), default)).Entry!.Id;
        return (banana, soup);
    }

    [Fact]
    public async Task Search_Meaning_RanksTheSemanticallyCloserEntryFirst()
    {
        var (s, _, _) = Make();
        var (banana, _) = await Fill(s);

        var r = await s.SearchAsync("sweet dessert", null, SearchMode.Meaning, 5, default);

        Assert.Equal("meaning", r.ModeUsed);
        Assert.Equal(banana, r.Hits[0].Entry.Id);
    }

    [Fact]
    public async Task Search_Meaning_WithoutAnEmbedderIsUnavailable()
    {
        var (s, _, _) = Make(withEmbedder: false);
        await Fill(s);
        await Assert.ThrowsAsync<MeaningUnavailableException>(
            () => s.SearchAsync("sweet", null, SearchMode.Meaning, 5, default));
    }

    [Fact]
    public async Task Search_Meaning_WhenTheEmbedderFailsIsUnavailable()
    {
        var (s, _, e) = Make();
        await Fill(s);
        e!.Fail = true;
        await Assert.ThrowsAsync<MeaningUnavailableException>(
            () => s.SearchAsync("sweet", null, SearchMode.Meaning, 5, default));
    }

    [Fact]
    public async Task Search_Hybrid_WithoutAnEmbedderAnswersTextAndSaysSo()
    {
        var (s, _, _) = Make(withEmbedder: false);
        var (_, soup) = await Fill(s);

        var r = await s.SearchAsync("leek", null, SearchMode.Hybrid, 5, default);

        Assert.Equal("text", r.ModeUsed);
        Assert.Equal(soup, r.Hits.Single().Entry.Id);
    }

    [Fact]
    public async Task Search_Hybrid_WhenTheEmbedderFailsAnswersTextAndSaysSo()
    {
        var (s, _, e) = Make();
        var (_, soup) = await Fill(s);
        e!.Fail = true;

        var r = await s.SearchAsync("leek", null, SearchMode.Hybrid, 5, default);

        Assert.Equal("text", r.ModeUsed);
        Assert.Equal(soup, r.Hits.Single().Entry.Id);
    }

    [Fact]
    public async Task Search_Hybrid_KeepsAnEntryFoundOnlyByTextAndOneFoundOnlyByMeaning()
    {
        var (s, _, _) = Make();
        var (banana, soup) = await Fill(s);

        // "cream" is a word only in the soup body; "dessert" is a concept only the banana entry shares.
        var r = await s.SearchAsync("cream dessert", null, SearchMode.Hybrid, 5, default);

        Assert.Equal("hybrid", r.ModeUsed);
        Assert.Contains(r.Hits, h => h.Entry.Id == soup);
        Assert.Contains(r.Hits, h => h.Entry.Id == banana);
    }

    [Fact]
    public async Task Search_Hybrid_AnEntryInBothListsOutranksOneInOne()
    {
        var (s, _, _) = Make();
        var both = (await s.SaveAsync(New("Banana cake", "sweet banana cake"), default)).Entry!.Id;
        await s.SaveAsync(New("Cake pan", "grease the pan"), default);

        var r = await s.SearchAsync("banana cake", null, SearchMode.Hybrid, 5, default);

        Assert.Equal(both, r.Hits[0].Entry.Id);
    }

    [Fact]
    public async Task Search_LimitAndKindFilterApply()
    {
        var (s, _, _) = Make();
        await Fill(s);
        await s.SaveAsync(new EntryInput(null, null, "technique", "Folding cake batter", "fold sweet batter", [], null, null), default);

        var r = await s.SearchAsync("sweet", "technique", SearchMode.Hybrid, 1, default);

        Assert.Single(r.Hits);
        Assert.Equal("technique", r.Hits[0].Entry.Kind);
    }

    [Fact]
    public async Task Search_SavingWhileTheEmbedderIsDownStillSavesAndReindexFillsTheGap()
    {
        var (s, store, e) = Make();
        e!.Fail = true;
        var banana = (await s.SaveAsync(New("Banana loaf", "sugar"), default)).Entry!.Id;
        Assert.Single(store.WithoutVector(e.Model, 10));

        e.Fail = false;
        await s.ReindexMissingAsync(default);

        Assert.Empty(store.WithoutVector(e.Model, 10));
        var r = await s.SearchAsync("dessert", null, SearchMode.Meaning, 5, default);
        Assert.Equal(banana, r.Hits[0].Entry.Id);
    }

    [Fact]
    public async Task Lookup_RecordsOnlyWhenTheEntryExists()
    {
        var (s, store, _) = Make(withEmbedder: false);
        var (banana, _) = await Fill(s);
        Assert.NotNull(s.Lookup(banana));
        Assert.Null(s.Lookup("e_missing"));
        Assert.Equal(0, store.GetStats().OlderLookups);
    }
}
