using MailSearch.Config;

namespace MailSearch.Embeddings;

public static class EmbeddingProviderFactory
{
    public static Task<IEmbeddingProvider> CreateAsync(EmbeddingConfig config, DataPaths paths, CancellationToken ct) =>
        config.Provider switch
        {
            EmbeddingProviderKind.Onnx => OnnxEmbeddingProvider.CreateAsync(config.Onnx, paths, ct).ContinueWith(t => (IEmbeddingProvider)t.Result, ct),
            EmbeddingProviderKind.Http => Task.FromResult<IEmbeddingProvider>(new HttpEmbeddingProvider(config.Http)),
            _ => throw new NotSupportedException($"Unknown embedding provider {config.Provider}"),
        };
}
