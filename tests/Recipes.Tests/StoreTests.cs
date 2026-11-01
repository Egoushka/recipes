using Microsoft.Extensions.Time.Testing;
using Recipes.Domain;
using Recipes.Storage;

namespace Recipes.Tests;

public class StoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static JournalStore NewStore(FakeTimeProvider? clock = null) =>
        new("Data Source=:memory:", clock ?? new FakeTimeProvider(T0));

    private static EntryInput New(string title, string body = "", string kind = "recipe", params string[] tags) =>
        new(null, null, kind, title, body, tags, null, null);

    [Fact]
    public void Store_SaveCreatesVersionOneThenTwoAndKeepsTheOldVersionReadable()
    {
        using var store = NewStore();
        var first = store.Save(New("Banana bread", "250 g flour"));
        Assert.Equal(SaveStatus.Saved, first.Status);
        Assert.Equal(1, first.Entry!.Version);
        Assert.StartsWith("e_", first.Entry.Id, StringComparison.Ordinal);
        Assert.Equal(14, first.Entry.Id.Length);

        var second = store.Save(new EntryInput(first.Entry.Id, 1, "recipe", "Banana bread", "300 g flour", [], null, null));
        Assert.Equal(SaveStatus.Saved, second.Status);
        Assert.Equal(2, second.Entry!.Version);

        Assert.Equal("300 g flour", store.Get(first.Entry.Id)!.Body);
        Assert.Equal("250 g flour", store.GetVersion(first.Entry.Id, 1)!.Body);
        Assert.Equal([1L, 2L], store.Versions(first.Entry.Id).Select(v => v.Version));
    }

    [Fact]
    public void Store_WrongBaseVersionIsAConflictAndChangesNothing()
    {
        using var store = NewStore();
        var id = store.Save(New("Soup")).Entry!.Id;
        store.Save(new EntryInput(id, 1, "recipe", "Soup", "v2", [], null, null));

        var stale = store.Save(new EntryInput(id, 1, "recipe", "Soup", "stale", [], null, null));

        Assert.Equal(SaveStatus.Conflict, stale.Status);
        Assert.Equal(2, stale.CurrentVersion);
        Assert.Equal("v2", store.Get(id)!.Body);
    }

    [Fact]
    public void Store_UnknownIdOrMissingBaseVersionIsRefused()
    {
        using var store = NewStore();
        Assert.Equal(SaveStatus.NotFound,
            store.Save(new EntryInput("e_nope", 1, "recipe", "x", "", [], null, null)).Status);

        var id = store.Save(New("Soup")).Entry!.Id;
        var noBase = store.Save(new EntryInput(id, null, "recipe", "Soup", "", [], null, null));
        Assert.Equal(SaveStatus.Invalid, noBase.Status);
        Assert.Contains(noBase.Errors, e => e.Path == "version");
    }

    [Fact]
    public void Store_InvalidInputIsReportedAndNotStored()
    {
        using var store = NewStore();
        var r = store.Save(New("", kind: "soup"));
        Assert.Equal(SaveStatus.Invalid, r.Status);
        Assert.Contains(r.Errors, e => e.Path == "kind");
        Assert.Contains(r.Errors, e => e.Path == "title");
    }

    [Fact]
    public void Store_DataThatIsNotAnObjectIsRefused()
    {
        using var store = NewStore();
        var r = store.Save(New("x") with { Data = System.Text.Json.Nodes.JsonNode.Parse("[1,2]") });
        Assert.Equal(SaveStatus.Invalid, r.Status);
        Assert.Contains(r.Errors, e => e.Path == "data");
    }

    [Fact]
    public void Search_Text_FindsByBodyWordAndByTagAndFiltersByKind()
    {
        using var store = NewStore();
        var bread = store.Save(New("Banana bread", "ripe bananas and walnuts", "recipe", "baking")).Entry!;
        var fold = store.Save(New("Folding", "fold gently", "technique", "baking", "dough")).Entry!;

        Assert.Equal(bread.Id, store.SearchText("walnuts", null, 10).Single().Entry.Id);
        Assert.Equal(2, store.SearchText("baking", null, 10).Count);
        Assert.Equal(fold.Id, store.SearchText("baking", "technique", 10).Single().Entry.Id);
        Assert.Empty(store.SearchText("quinoa", null, 10));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("AND OR NOT")]
    [InlineData("a* ( b")]
    [InlineData("   ")]
    public void Search_Text_QueryWithFtsSyntaxNeverThrows(string query)
    {
        using var store = NewStore();
        store.Save(New("Banana bread", "a b"));
        _ = store.SearchText(query, null, 10);
    }

    [Fact]
    public void Search_Text_NewVersionReplacesTheOldWordsInTheIndex()
    {
        using var store = NewStore();
        var e = store.Save(New("Soup", "leeks")).Entry!;
        store.Save(new EntryInput(e.Id, 1, "recipe", "Soup", "carrots", [], null, null));

        Assert.Empty(store.SearchText("leeks", null, 10));
        Assert.Single(store.SearchText("carrots", null, 10));
    }

    [Fact]
    public void Seed_FreshDatabaseHasTheOvenDialUnmeasured()
    {
        using var store = NewStore();
        store.SeedIfMissing();

        var seed = store.Get("e_oven-dial")!;
        Assert.Equal("config", seed.Kind);
        Assert.False(seed.Data!["measured"]!.GetValue<bool>());
        Assert.Equal(1, seed.Version);
        Assert.Equal(200, store.NewestDial()!.Data!["marks"]!["6"]!.GetValue<int>());
    }

    [Fact]
    public void Seed_StartingTwiceKeepsOneCopyAndAnEditSurvives()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recipes-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new JournalStore($"Data Source={path}", TimeProvider.System))
            {
                store.SeedIfMissing();
                var seed = store.Get("e_oven-dial")!;
                store.Save(new EntryInput(seed.Id, 1, "config", seed.Title, "measured with a thermometer", [], seed.Data, null));
            }

            using (var again = new JournalStore($"Data Source={path}", TimeProvider.System))
            {
                again.SeedIfMissing();
                Assert.Equal(2, again.Get("e_oven-dial")!.Version);
                Assert.Equal("measured with a thermometer", again.Get("e_oven-dial")!.Body);
                Assert.Single(again.SearchText("oven", null, 10), h => h.Entry.Id == "e_oven-dial");
            }
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                File.Delete(f);
        }
    }

    [Fact]
    public void Stats_NotStartedUntilTheFirstNonSeedSave()
    {
        using var store = NewStore();
        store.SeedIfMissing();
        var s = store.GetStats();
        Assert.Equal("not_started", s.Status);
        Assert.Equal(0, s.EntriesSaved);
        Assert.False(s.Pass);
    }

    [Fact]
    public void Stats_OnlyLookupsOfEntriesOlderThan24HoursCount()
    {
        var clock = new FakeTimeProvider(T0);
        using var store = NewStore(clock);
        var id = store.Save(New("Soup")).Entry!.Id;

        store.RecordLookup(id);
        Assert.Equal(0, store.GetStats().OlderLookups);

        clock.Advance(TimeSpan.FromHours(25));
        store.RecordLookup(id);
        store.RecordLookup(id);
        Assert.Equal(2, store.GetStats().OlderLookups);
    }

    [Fact]
    public void Stats_PassesAtTenSavesAndTwoOlderLookupsAndTheSeedIsNotASave()
    {
        var clock = new FakeTimeProvider(T0);
        using var store = NewStore(clock);
        store.SeedIfMissing();
        var ids = Enumerable.Range(1, 9).Select(i => store.Save(New($"Entry {i}")).Entry!.Id).ToList();

        clock.Advance(TimeSpan.FromDays(2));
        store.RecordLookup(ids[0]);
        store.RecordLookup("e_oven-dial");
        var nine = store.GetStats();
        Assert.Equal(9, nine.EntriesSaved);
        Assert.Equal(2, nine.OlderLookups);
        Assert.False(nine.Pass);

        store.Save(New("Entry 10"));
        var ten = store.GetStats();
        Assert.Equal(10, ten.EntriesSaved);
        Assert.True(ten.Pass);
        Assert.Equal("passed", ten.Status);
    }

    [Fact]
    public void Stats_FailsOnlyAfterTheFourteenDayWindowEnds()
    {
        var clock = new FakeTimeProvider(T0);
        using var store = NewStore(clock);
        store.Save(New("Soup"));

        clock.Advance(TimeSpan.FromDays(13));
        Assert.Equal("running", store.GetStats().Status);

        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal("failed", store.GetStats().Status);
    }

    [Fact]
    public void Store_ActionAnswerIsStoredOnceUnderItsActionId()
    {
        using var store = NewStore();
        Assert.Null(store.GetAction("01ABC"));
        store.PutAction("01ABC", 200, "{\"a\":1}");
        store.PutAction("01ABC", 200, "{\"a\":2}");
        Assert.Equal((200, "{\"a\":1}"), store.GetAction("01ABC"));
    }
}
