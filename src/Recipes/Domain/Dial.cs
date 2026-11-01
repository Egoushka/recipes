using System.Globalization;
using System.Text.Json.Nodes;

namespace Recipes.Domain;

/// <summary>Where a temperature sits on the oven dial. <c>Position</c> is null when <c>Error</c> says why.</summary>
public sealed record DialResult(double? Position, int Set, int LowMark, int HighMark, string? Error);

/// <summary>The oven dial: marks to °C, whole numbers, strictly increasing. Never extrapolates.</summary>
public sealed class Dial
{
    public const int MinCelsius = 50;
    public const int MaxCelsius = 400;

    private readonly (int Mark, int Celsius)[] _marks;

    private Dial((int Mark, int Celsius)[] marks) => _marks = marks;

    public IReadOnlyList<(int Mark, int Celsius)> Marks => _marks;

    public static bool TryParse(JsonObject data, out Dial? dial, List<FieldError> errors)
    {
        dial = null;
        var before = errors.Count;

        if (data["marks"] is not JsonObject marks || marks.Count < 2)
        {
            errors.Add(new("data.marks", "Give at least two marks as an object such as {\"1\": 140, \"2\": 150}."));
            return false;
        }

        var parsed = new List<(int Mark, int Celsius)>();
        foreach (var (key, node) in marks)
        {
            var path = $"data.marks.{key}";
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var mark) || mark < 1)
            {
                errors.Add(new(path, "A mark is a whole number from 1 up."));
                continue;
            }

            if (!TryWholeNumber(node, out var celsius))
            {
                errors.Add(new(path, "Degrees must be a whole number."));
                continue;
            }

            if (celsius is < MinCelsius or > MaxCelsius)
            {
                errors.Add(new(path, $"Degrees must be between {MinCelsius} and {MaxCelsius}."));
                continue;
            }

            parsed.Add((mark, celsius));
        }

        if (errors.Count > before)
            return false;

        parsed.Sort((a, b) => a.Mark.CompareTo(b.Mark));
        for (var i = 1; i < parsed.Count; i++)
        {
            if (parsed[i].Celsius <= parsed[i - 1].Celsius)
            {
                errors.Add(new($"data.marks.{parsed[i].Mark}",
                    $"Degrees must rise with the mark: mark {parsed[i].Mark} is {parsed[i].Celsius} but mark {parsed[i - 1].Mark} is {parsed[i - 1].Celsius}."));
                return false;
            }
        }

        dial = new Dial([.. parsed]);
        return true;
    }

    public DialResult Convert(double celsius)
    {
        var low = _marks[0];
        var high = _marks[^1];
        if (celsius < low.Celsius || celsius > high.Celsius)
        {
            return new(null, 0, 0, 0,
                $"{celsius.ToString(CultureInfo.InvariantCulture)} °C is outside the marked range {low.Celsius}-{high.Celsius} °C; the dial is not extrapolated.");
        }

        for (var i = 0; i < _marks.Length - 1; i++)
        {
            var (m0, c0) = _marks[i];
            var (m1, c1) = _marks[i + 1];
            if (celsius > c1)
                continue;

            var position = m0 + (celsius - c0) / (c1 - c0) * (m1 - m0);
            return new(position, (int)Math.Floor(position + 0.5), m0, m1, null);
        }

        return new(high.Mark, high.Mark, high.Mark, high.Mark, null);
    }

    private static bool TryWholeNumber(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue v)
            return false;
        if (v.TryGetValue<int>(out value))
            return true;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < int.MaxValue)
        {
            value = (int)d;
            return true;
        }

        return false;
    }
}
