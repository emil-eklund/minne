using MailSearch.Config;

namespace MailSearch.Embeddings;

/// <summary>Fetches model files from the Hugging Face hub into the local models directory (once).</summary>
public static class ModelDownloader
{
    public static string ResolveModelDirectory(OnnxEmbeddingConfig config, DataPaths paths) =>
        ResolveModelDirectory(config.ModelDirectory, config.ModelRepo, paths);

    public static string ResolveModelDirectory(string? modelDirectory, string modelRepo, DataPaths paths) =>
        modelDirectory ?? Path.Combine(paths.ModelsDirectory, modelRepo.Replace('/', '_'));

    public static Task<(string ModelPath, string TokenizerPath)> EnsureAsync(
        OnnxEmbeddingConfig config, DataPaths paths, CancellationToken ct) =>
        EnsureAsync(config.ModelDirectory, config.ModelRepo, config.ModelFile, config.TokenizerFile, paths, ct);

    /// <summary>The SentencePiece model every XLM-R repo ships, and what the managed tokenizer reads.</summary>
    private const string SentencePieceFile = "sentencepiece.bpe.model";

    /// <summary>
    /// A config.json written before the tokenizer moved to SentencePiece still names tokenizer.json,
    /// which the managed tokenizer cannot read. Substitute the SentencePiece model instead of making
    /// the user hand-edit their config; the ids are identical, so nothing needs re-indexing.
    /// </summary>
    private static string MigrateTokenizerFile(string tokenizerFile) =>
        Path.GetExtension(tokenizerFile).Equals(".json", StringComparison.OrdinalIgnoreCase) ? SentencePieceFile : tokenizerFile;

    public static async Task<(string ModelPath, string TokenizerPath)> EnsureAsync(
        string? modelDirectory, string modelRepo, string modelFile, string tokenizerFile, DataPaths paths, CancellationToken ct)
    {
        tokenizerFile = MigrateTokenizerFile(tokenizerFile);
        var dir = ResolveModelDirectory(modelDirectory, modelRepo, paths);
        var modelPath = Path.Combine(dir, modelFile.Replace('/', Path.DirectorySeparatorChar));
        var tokenizerPath = Path.Combine(dir, tokenizerFile.Replace('/', Path.DirectorySeparatorChar));

        if (modelDirectory is not null)
        {
            if (!File.Exists(modelPath) || !File.Exists(tokenizerPath))
            {
                // A folder prepared for an older release has tokenizer.json and nothing else to say why it is rejected.
                var hint = tokenizerFile == SentencePieceFile && File.Exists(Path.Combine(dir, "tokenizer.json"))
                    ? $" {SentencePieceFile} replaced tokenizer.json; it comes from the same Hugging Face repo."
                    : "";
                throw new FileNotFoundException($"Model directory {dir} must contain {modelFile} and {tokenizerFile}.{hint}");
            }
            return (modelPath, tokenizerPath);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        await DownloadIfMissingAsync(http, modelRepo, modelFile, modelPath, ct);
        await DownloadIfMissingAsync(http, modelRepo, tokenizerFile, tokenizerPath, ct);
        return (modelPath, tokenizerPath);
    }

    private static async Task DownloadIfMissingAsync(HttpClient http, string repo, string file, string target, CancellationToken ct)
    {
        if (File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var url = $"https://huggingface.co/{repo}/resolve/main/{file}";
        Console.Error.WriteLine($"Downloading {url}");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Could not download {url}: {(int)response.StatusCode} {response.ReasonPhrase}");

        var total = response.Content.Headers.ContentLength;
        var temp = target + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var dest = File.Create(temp))
        {
            var buffer = new byte[1 << 16];
            long done = 0; var lastReport = DateTime.UtcNow;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total is > 0 && DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1))
                {
                    Console.Error.Write($"\r  {done / 1048576.0:0} / {total / 1048576.0:0} MB");
                    lastReport = DateTime.UtcNow;
                }
            }
        }
        if (total is > 0) Console.Error.WriteLine();
        File.Move(temp, target, overwrite: true);
    }
}
