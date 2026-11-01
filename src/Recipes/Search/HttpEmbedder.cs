using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Recipes.Search;

/// <summary>Any OpenAI-compatible <c>/v1/embeddings</c> endpoint.</summary>
public sealed class HttpEmbedder : IEmbedder
{
    private readonly HttpClient _http;

    public HttpEmbedder(HttpClient http, string url, string model, string? key)
    {
        _http = http;
        Url = new Uri(url);
        Model = model;
        if (!string.IsNullOrEmpty(key))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string Model { get; }

    private Uri Url { get; }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(Url, new { model = Model, input = text }, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EmbeddingsResponse>(ct);
        return body?.Data is [{ Embedding: { Length: > 0 } v }, ..]
            ? v
            : throw new InvalidOperationException("The embeddings endpoint returned no vector.");
    }

    private sealed record EmbeddingsResponse([property: JsonPropertyName("data")] List<Item>? Data);

    private sealed record Item([property: JsonPropertyName("embedding")] float[]? Embedding);
}
