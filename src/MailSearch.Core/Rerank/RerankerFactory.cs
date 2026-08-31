using MailSearch.Config;

namespace MailSearch.Rerank;

public static class RerankerFactory
{
    public static Task<IReranker> CreateAsync(RerankConfig config, DataPaths paths, CancellationToken ct) =>
        OnnxReranker.CreateAsync(config.Onnx, paths, ct).ContinueWith(t => (IReranker)t.Result, ct);
}
