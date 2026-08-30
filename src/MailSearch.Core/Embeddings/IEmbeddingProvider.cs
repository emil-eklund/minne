namespace MailSearch.Embeddings;

/// <summary>Turns text into dense vectors. Implementations must be deterministic for a given model.</summary>
public interface IEmbeddingProvider : IDisposable
{
    /// <summary>Stable identifier (model name) stored in the index so mismatches can be detected.</summary>
    string ModelId { get; }
    int Dimensions { get; }
    int BatchSize { get; }

    /// <summary>Embed texts that will be indexed.</summary>
    Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct);

    /// <summary>Embed a search query (some models use a different prefix for queries).</summary>
    Task<float[]> EmbedQueryAsync(string text, CancellationToken ct);
}
