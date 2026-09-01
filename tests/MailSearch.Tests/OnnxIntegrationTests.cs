using MailSearch.Config;
using MailSearch.Embeddings;

namespace MailSearch.Tests;

/// <summary>
/// Downloads the default model (~470 MB) and checks that multilingual paraphrases land close together.
/// Opt in with MINNE_RUN_MODEL_TESTS=1; reuses the normal models directory so the download happens once.
/// </summary>
public class OnnxIntegrationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MINNE_RUN_MODEL_TESTS") == "1";

    [SkippableFact]
    public async Task Default_model_embeds_multilingual_paraphrases_close_together()
    {
        Skip.IfNot(Enabled, "set MINNE_RUN_MODEL_TESTS=1 to run");
        var paths = new DataPaths();
        using var provider = await OnnxEmbeddingProvider.CreateAsync(new OnnxEmbeddingConfig(), paths, CancellationToken.None);
        Assert.Equal(384, provider.Dimensions);

        var texts = new[]
        {
            "kick-off agenda for the project",   // 0
            "schedule for the project kickoff",  // 1 paraphrase
            "agenda för projektets kickoff",     // 2 Swedish paraphrase
            "invoice payment is overdue",        // 3 unrelated
        };
        var v = await provider.EmbedDocumentsAsync(texts, CancellationToken.None);
        float Sim(int a, int b) => System.Numerics.Tensors.TensorPrimitives.Dot(v[a], v[b]);

        Assert.True(Sim(0, 1) > Sim(0, 3), $"paraphrase {Sim(0, 1)} vs unrelated {Sim(0, 3)}");
        Assert.True(Sim(0, 2) > Sim(0, 3), $"swedish {Sim(0, 2)} vs unrelated {Sim(0, 3)}");
        Assert.InRange(System.Numerics.Tensors.TensorPrimitives.Norm(v[0]), 0.99f, 1.01f);
    }

    /// <summary>
    /// The tail sentence sits far past the 128-token budget. With the old truncation the long text
    /// and the filler-only text produced identical vectors (same first 128 tokens); with windowed
    /// embedding the tail must pull the vector measurably toward its content.
    /// </summary>
    [SkippableFact]
    public async Task Windowing_represents_content_past_the_token_budget()
    {
        Skip.IfNot(Enabled, "set MINNE_RUN_MODEL_TESTS=1 to run");
        var paths = new DataPaths();
        using var provider = await OnnxEmbeddingProvider.CreateAsync(new OnnxEmbeddingConfig(), paths, CancellationToken.None);

        var filler = string.Join(" ", Enumerable.Repeat(
            "The committee reviewed routine administrative paperwork during the quarterly meeting.", 30));
        var vectors = await provider.EmbedDocumentsAsync(
            [filler + " The wire transfer failed because the IBAN contained a typo.", filler], CancellationToken.None);
        var query = await provider.EmbedQueryAsync("payment failed due to a wrong bank account number", CancellationToken.None);

        float Sim(float[] a, float[] b) => System.Numerics.Tensors.TensorPrimitives.Dot(a, b);
        Assert.True(Sim(vectors[0], query) > Sim(vectors[1], query),
            $"with tail {Sim(vectors[0], query)} vs filler only {Sim(vectors[1], query)}");
        Assert.InRange(System.Numerics.Tensors.TensorPrimitives.Norm(vectors[0]), 0.99f, 1.01f);
    }

    [SkippableFact]
    public async Task Default_reranker_scores_relevant_passages_higher()
    {
        Skip.IfNot(Enabled, "set MINNE_RUN_MODEL_TESTS=1 to run");
        var paths = new DataPaths();
        using var reranker = await MailSearch.Rerank.OnnxReranker.CreateAsync(new OnnxRerankConfig(), paths, CancellationToken.None);
        var scores = await reranker.ScoreAsync("How many people live in Berlin?",
        [
            "Berlin has a population of 3.5 million registered inhabitants.", // relevant
            "A recipe for a simple chocolate cake with dark chocolate.",      // unrelated
            "Berlin har ungefär 3,5 miljoner invånare.",                      // relevant, Swedish
        ], CancellationToken.None);
        Assert.True(scores[0] > scores[1], $"relevant {scores[0]} vs unrelated {scores[1]}");
        Assert.True(scores[2] > scores[1], $"swedish {scores[2]} vs unrelated {scores[1]}");
    }

    [SkippableFact]
    public async Task Provider_embeds_identically_after_unload()
    {
        Skip.IfNot(Enabled, "set MINNE_RUN_MODEL_TESTS=1 to run");
        var paths = new DataPaths();
        using var provider = await OnnxEmbeddingProvider.CreateAsync(new OnnxEmbeddingConfig(), paths, CancellationToken.None);
        var before = await provider.EmbedQueryAsync("kick-off agenda for the project", CancellationToken.None);
        provider.Unload();
        var after = await provider.EmbedQueryAsync("kick-off agenda for the project", CancellationToken.None);
        Assert.Equal(before, after);
    }
}
