namespace MailSearch.Rerank;

public interface IReranker : IDisposable
{
    string ModelId { get; }

    /// <summary>Relevance score for each (query, passage) pair; higher = more relevant.</summary>
    Task<float[]> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken ct);
}
