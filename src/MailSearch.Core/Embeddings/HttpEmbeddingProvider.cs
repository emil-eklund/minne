using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailSearch.Config;

namespace MailSearch.Embeddings;

/// <summary>Calls an OpenAI-compatible embeddings endpoint (Ollama, LM Studio, vLLM, Azure OpenAI, ...).</summary>
public sealed class HttpEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly HttpEmbeddingConfig _config;
    private int _dimensions;

    public string ModelId => $"http:{_config.Model}";
    public int Dimensions => _dimensions == 0 ? _dimensions = EmbedQueryAsync("dimension probe", CancellationToken.None).GetAwaiter().GetResult().Length : _dimensions;
    public int BatchSize => _config.BatchSize;

    public HttpEmbeddingProvider(HttpEmbeddingConfig config, HttpClient? http = null)
    {
        _config = config;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrEmpty(config.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    public async Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var result = new float[texts.Count][];
        for (var offset = 0; offset < texts.Count; offset += _config.BatchSize)
        {
            var batch = texts.Skip(offset).Take(_config.BatchSize).Select(t => _config.DocumentPrefix + t).ToList();
            var vectors = await CallAsync(batch, ct);
            for (var i = 0; i < vectors.Length; i++) result[offset + i] = vectors[i];
        }
        return result;
    }

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken ct) =>
        (await CallAsync([_config.QueryPrefix + text], ct))[0];

    private async Task<float[][]> CallAsync(IReadOnlyList<string> input, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(_config.Endpoint, new { model = _config.Model, input }, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Embedding endpoint returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var data = doc.RootElement.GetProperty("data").EnumerateArray()
            .OrderBy(e => e.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0)
            .Select(e => e.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray())
            .ToArray();
        foreach (var v in data) Normalize(v);
        if (data.Length > 0) _dimensions = data[0].Length;
        return data;
    }

    private static void Normalize(float[] v)
    {
        var norm = System.Numerics.Tensors.TensorPrimitives.Norm(v);
        if (norm > 0) System.Numerics.Tensors.TensorPrimitives.Divide(v, norm, v);
    }

    public void Dispose() => _http.Dispose();
}
