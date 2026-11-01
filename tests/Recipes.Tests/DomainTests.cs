using System.Text.Json.Nodes;
using Recipes.Domain;

namespace Recipes.Tests;

public class DomainTests
{
    private static JsonObject SeedMarks() => (JsonObject)JsonNode.Parse(
        """{"type":"dial","measured":false,"marks":{"1":140,"2":150,"3":170,"4":180,"5":190,"6":200,"7":220,"8":230,"9":240}}""")!;

    private static EntryInput Valid() =>
        new(null, null, "recipe", "Banana bread", "Mash, fold, bake.", ["baking"], null, null);

    [Fact]
    public void Dial_OnAMarkGivesTheMark()
    {
        Assert.True(Dial.TryParse(SeedMarks(), out var dial, []));
        var r = dial!.Convert(200);
        Assert.Equal(6.0, r.Position!.Value, 6);
        Assert.Equal(6, r.Set);
    }

    [Fact]
    public void Dial_BetweenMarksInterpolatesAndRoundsHalfUp()
    {
        Assert.True(Dial.TryParse(SeedMarks(), out var dial, []));
        var r = dial!.Convert(175);
        Assert.Equal(3.5, r.Position!.Value, 6);
        Assert.Equal(4, r.Set);
        Assert.Equal((3, 4), (r.LowMark, r.HighMark));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(300)]
    public void Dial_OutsideTheMarksIsAnErrorNotAnExtrapolation(double celsius)
    {
        Assert.True(Dial.TryParse(SeedMarks(), out var dial, []));
        var r = dial!.Convert(celsius);
        Assert.Null(r.Position);
        Assert.Contains("140", r.Error);
        Assert.Contains("240", r.Error);
    }

    [Fact]
    public void Dial_NonMonotonicMarksAreRefusedWithTheFieldNamed()
    {
        var data = (JsonObject)JsonNode.Parse("""{"type":"dial","marks":{"1":140,"2":150,"3":145}}""")!;
        var errors = new List<FieldError>();
        Assert.False(Dial.TryParse(data, out _, errors));
        Assert.Contains(errors, e => e.Path == "data.marks.3");
    }

    [Theory]
    [InlineData("""{"type":"dial","marks":{"1":140}}""", "data.marks")]
    [InlineData("""{"type":"dial","marks":{"1":20,"2":150}}""", "data.marks.1")]
    [InlineData("""{"type":"dial","marks":{"1":140.5,"2":150}}""", "data.marks.1")]
    [InlineData("""{"type":"dial","marks":{"x":140,"2":150}}""", "data.marks.x")]
    [InlineData("""{"type":"dial"}""", "data.marks")]
    public void Dial_MalformedMarksAreRefused(string json, string path)
    {
        var errors = new List<FieldError>();
        Assert.False(Dial.TryParse((JsonObject)JsonNode.Parse(json)!, out _, errors));
        Assert.Contains(errors, e => e.Path == path);
    }

    [Fact]
    public void Validation_AcceptsAValidEntry() =>
        Assert.Empty(EntryValidator.Validate(Valid(), data: null));

    [Fact]
    public void Validation_BadKindNamesTheField() =>
        Assert.Contains(EntryValidator.Validate(Valid() with { Kind = "soup" }, null), e => e.Path == "kind");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validation_EmptyTitleNamesTheField(string title) =>
        Assert.Contains(EntryValidator.Validate(Valid() with { Title = title }, null), e => e.Path == "title");

    [Fact]
    public void Validation_TooManyTagsNamesTheField() =>
        Assert.Contains(
            EntryValidator.Validate(Valid() with { Tags = Enumerable.Range(0, 21).Select(i => $"t{i}").ToList() }, null),
            e => e.Path == "tags");

    [Fact]
    public void Validation_LongTagNamesTheIndex() =>
        Assert.Contains(
            EntryValidator.Validate(Valid() with { Tags = [new string('a', 41)] }, null),
            e => e.Path == "tags.0");

    [Fact]
    public void Validation_DataOver8KbNamesTheField()
    {
        var big = new JsonObject { ["blob"] = new string('x', 8200) };
        Assert.Contains(EntryValidator.Validate(Valid(), big), e => e.Path == "data");
    }

    [Theory]
    [InlineData("http://example.com/r")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    public void Validation_NonHttpsSourceNamesTheField(string source) =>
        Assert.Contains(EntryValidator.Validate(Valid() with { Source = source }, null), e => e.Path == "source");

    [Fact]
    public void Validation_DialConfigWithBadMarksIsRefused()
    {
        var input = Valid() with { Kind = "config" };
        var data = (JsonObject)JsonNode.Parse("""{"type":"dial","marks":{"1":200,"2":150}}""")!;
        Assert.Contains(EntryValidator.Validate(input, data), e => e.Path.StartsWith("data.marks", StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_TagsAreNormalisedToLowerCaseWithoutDuplicates() =>
        Assert.Equal(["baking", "oven"], EntryValidator.NormalizeTags([" Baking", "OVEN", "baking", ""]));
}
