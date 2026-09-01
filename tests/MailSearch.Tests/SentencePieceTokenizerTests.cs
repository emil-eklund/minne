using MailSearch.Config;
using MailSearch.Embeddings;

namespace MailSearch.Tests;

/// <summary>
/// Checks the tokenizer against ids frozen from Hugging Face's tokenizer.json implementation.
/// Needs only the ~5 MB sentencepiece model, never the ONNX weights, so it stays cheap.
/// Opt in with MINNE_RUN_MODEL_TESTS=1.
/// </summary>
public class SentencePieceTokenizerTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MINNE_RUN_MODEL_TESTS") == "1";

    [SkippableFact]
    public async Task Tokenizer_reproduces_the_golden_ids()
    {
        Skip.IfNot(Enabled, "set MINNE_RUN_MODEL_TESTS=1 to run");
        var paths = new DataPaths();
        foreach (var model in TokenizerGolden.Load().Models)
        {
            var path = await EnsureSentencePieceModelAsync(model.ModelRepo, paths, CancellationToken.None);
            using var tokenizer = new SentencePieceTokenizerAdapter(path);
            Assert.Equal(model.PadTokenId, tokenizer.PadTokenId);
            Assert.Equal(model.DoublePairSeparator, tokenizer.DoublePairSeparator);
            // fails loudly if a case was added without regenerating the fixture
            Assert.Equal(TokenizerEquivalenceCases.All, model.Cases.Select(c => c.Text));
            foreach (var golden in model.Cases)
                Assert.Equal(golden.TokenIds, TokenizerEquivalenceCases.Truncate(tokenizer.Encode(golden.Text), model.MaxTokens));
        }
    }

    // Not ModelDownloader.EnsureAsync: that would also fetch the several-hundred-MB ONNX export.
    private static async Task<string> EnsureSentencePieceModelAsync(string repo, DataPaths paths, CancellationToken ct)
    {
        var file = new OnnxEmbeddingConfig().TokenizerFile;
        var target = Path.Combine(ModelDownloader.ResolveModelDirectory(null, repo, paths), file);
        if (File.Exists(target)) return target;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        await File.WriteAllBytesAsync(target, await http.GetByteArrayAsync($"https://huggingface.co/{repo}/resolve/main/{file}", ct), ct);
        return target;
    }
}
