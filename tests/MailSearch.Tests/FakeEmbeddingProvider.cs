using System.Numerics.Tensors;
using MailSearch.Embeddings;

namespace MailSearch.Tests;

/// <summary>
/// Deterministic bag-of-words embedding: each lower-cased word hashes to a dimension.
/// Good enough to make "similar wording" rank higher without a real model.
/// </summary>
public sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    public string ModelId => "fake:bow";
    public int Dimensions => 256;
    public int BatchSize => 8;

    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        Task.FromResult(texts.Select(Embed).ToArray());

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct) => Task.FromResult(Embed(text));

    private float[] Embed(string text)
    {
        var v = new float[Dimensions];
        foreach (var word in text.ToLowerInvariant().Split([' ', '\n', ',', '.', ':', ';', '!', '?', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            // stem crudely so "kickoff"/"kickoffen" land close together
            var stem = word.Length > 5 ? word[..5] : word;
            v[StableHash(stem) % Dimensions] += 1;
        }
        var norm = TensorPrimitives.Norm(v);
        if (norm > 0) TensorPrimitives.Divide(v, norm, v);
        return v;
    }

    /// <summary>FNV-1a; string.GetHashCode is randomised per process and would make tests flaky.</summary>
    private static int StableHash(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) h = (h ^ c) * 16777619;
        return (int)(h & 0x7FFFFFFF);
    }

    public void Dispose() { }
}
