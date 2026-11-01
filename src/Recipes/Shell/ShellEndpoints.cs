using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Recipes.Mcp;
using Recipes.Search;
using Switchknife.McpGuard;

namespace Recipes.Shell;

internal static class ShellEndpoints
{
    /// <summary>How far the signed <c>at</c> may be from the server clock (the approvals contract).</summary>
    public static readonly TimeSpan ClockWindow = TimeSpan.FromMinutes(10);

    private static readonly string[] ScreenParams = ["entry_id"];

    public static void MapShell(this WebApplication app, string? token)
    {
        var shell = app.MapGroup("/shell").RequireMcpGuard(token);

        shell.MapGet("/manifest", (IConfiguration config) =>
            Results.Json(ShellScreens.Manifest(config["Shell:PublicUrl"] ?? "https://recipes.invalid")));

        shell.MapGet("/screens/{id}", (string id, HttpContext http, JournalService journal) => Screen(id, http, journal));

        shell.MapPost("/actions/{name}", (string name, HttpContext http, JournalService journal, IConfiguration config,
            TimeProvider clock, CancellationToken ct) => Act(name, http, journal, config, clock, ct));
    }

    private static IResult Screen(string id, HttpContext http, JournalService journal)
    {
        var entryId = http.Request.Query["entry_id"].ToString();
        foreach (var key in http.Request.Query.Keys)
        {
            if (!ScreenParams.Contains(key, StringComparer.Ordinal))
                return Results.BadRequest(new { error = $"Unknown parameter '{key}'." });
        }

        JsonObject doc;
        switch (id)
        {
            case "home":
                doc = ShellScreens.Home(journal.Store.Recent(ShellScreens.HomeLimit));
                break;
            case "oven":
                doc = ShellScreens.Oven(journal.Store.NewestDial());
                break;
            case "entry":
                {
                    if (entryId.Length == 0)
                        return Results.BadRequest(new { error = "entry_id is required." });
                    // Only this screen is a read; the edit form does not count as a lookup.
                    var entry = journal.Lookup(entryId);
                    if (entry is null)
                        return Results.NotFound(new { error = "No such entry." });
                    doc = ShellScreens.EntryView(entry);
                    break;
                }

            case "entry-new":
                {
                    var entry = entryId.Length == 0 ? null : journal.Store.Get(entryId);
                    if (entryId.Length > 0 && entry is null)
                        return Results.NotFound(new { error = "No such entry." });
                    doc = ShellScreens.EntryForm(entry);
                    break;
                }

            default:
                return Results.NotFound(new { error = "No such screen." });
        }

        var etag = doc["etag"]!.GetValue<string>();
        http.Response.Headers.ETag = etag;
        if (http.Request.Headers.IfNoneMatch.ToString().Split(',').Any(t => t.Trim() == etag || t.Trim() == "*"))
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Content(EntryJson.Text(doc), "application/json");
    }

    private static async Task<IResult> Act(string name, HttpContext http, JournalService journal, IConfiguration config,
        TimeProvider clock, CancellationToken ct)
    {
        JsonObject body;
        try
        {
            body = (await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct)) as JsonObject
                ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Body must be a JSON object." });
        }

        string? Str(string key) => body[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var actionId = Str("action_id");
        var bodyName = Str("name");
        var screen = Str("screen");
        var at = Str("at");
        if (actionId is null || screen is null || at is null || bodyName != name || body["context"] is not JsonObject context)
            return Results.BadRequest(new { error = "action_id, name (matching the URL), screen, at and context are required." });

        var key = config["Shell:ActionKey"];
        if (string.IsNullOrEmpty(key) || !Signing.Verify(key, actionId, name, screen, at, context, Str("sig")))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var signedAt)
            || (clock.GetUtcNow() - signedAt).Duration() > ClockWindow)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!ShellActions.Names.Contains(name, StringComparer.Ordinal))
            return Results.NotFound(new { error = "No such action." });

        var seen = journal.Store.GetAction(actionId);
        if (seen is { } previous)
        {
            http.Response.Headers["Idempotent-Replay"] = "true";
            return Results.Content(previous.Body, "application/json", statusCode: previous.Status);
        }

        var answer = await new ShellActions(journal).RunAsync(name, context, ct);
        var text = EntryJson.Text(answer.Body);
        journal.Store.PutAction(actionId, answer.Status, text);
        return Results.Content(text, "application/json", statusCode: answer.Status);
    }
}
