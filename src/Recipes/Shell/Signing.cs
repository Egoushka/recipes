using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recipes.Shell;

/// <summary>
/// Action signature of the approvals shell: <c>HMAC-SHA256(key, action_id|name|screen|at|h)</c>, where <c>h</c> is the
/// SHA-256 of the canonical context (keys sorted at every depth, no spaces; "" for an empty context).
/// Mirrors approvals <c>contract/shell/canonical.mjs</c>; the shared vectors pin it.
/// </summary>
public static class Signing
{
    public static string Canonical(JsonNode? context)
    {
        if (context is null or JsonObject { Count: 0 })
            return "";
        var sb = new StringBuilder();
        Write(sb, context);
        return sb.ToString();
    }

    public static string Hash(JsonNode? context)
    {
        var canonical = Canonical(context);
        return canonical.Length == 0 ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string Sign(string key, string actionId, string name, string screen, string at, JsonNode? context)
    {
        var payload = string.Join('|', actionId, name, screen, at, Hash(context));
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload)));
    }

    public static bool Verify(string key, string actionId, string name, string screen, string at, JsonNode? context, string? sig)
    {
        if (string.IsNullOrEmpty(sig) || sig.Length != 64)
            return false;
        var expected = Sign(key, actionId, name, screen, at, context);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig.ToLowerInvariant()));
    }

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    Write(sb, value);
                }

                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    Write(sb, arr[i]);
                }

                sb.Append(']');
                break;
            case JsonValue v:
                WriteValue(sb, v);
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, JsonValue v)
    {
        var element = JsonSerializer.SerializeToElement(v);
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                WriteString(sb, element.GetString()!);
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Number:
                sb.Append(Number(element));
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    /// <summary>Numbers as JavaScript prints them: 1.0 is "1". Integers beyond 2^53 are not exact in JS either.</summary>
    private static string Number(JsonElement e)
    {
        if (e.TryGetInt64(out var l))
            return l.ToString(CultureInfo.InvariantCulture);
        var d = e.GetDouble();
        return d == Math.Floor(d) && Math.Abs(d) < 1e21
            ? ((decimal)d).ToString(CultureInfo.InvariantCulture)
            : d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Like <c>JSON.stringify</c>: escapes quote, backslash and control characters only.</summary>
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }

        sb.Append('"');
    }
}
