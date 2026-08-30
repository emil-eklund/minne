using MailSearch.Config;

namespace MailSearch.Embeddings;

/// <summary>Fetches model files from the Hugging Face hub into the local models directory (once).</summary>
public static class ModelDownloader
{
    public static string ResolveModelDirectory(OnnxEmbeddingConfig config, DataPaths paths) =>
        config.ModelDirectory ?? Path.Combine(paths.ModelsDirectory, config.ModelRepo.Replace('/', '_'));

    public static async Task<(string ModelPath, string TokenizerPath)> EnsureAsync(
        OnnxEmbeddingConfig config, DataPaths paths, CancellationToken ct)
    {
        var dir = ResolveModelDirectory(config, paths);
        var modelPath = Path.Combine(dir, config.ModelFile.Replace('/', Path.DirectorySeparatorChar));
        var tokenizerPath = Path.Combine(dir, config.TokenizerFile.Replace('/', Path.DirectorySeparatorChar));

        if (config.ModelDirectory is not null)
        {
            if (!File.Exists(modelPath) || !File.Exists(tokenizerPath))
                throw new FileNotFoundException($"Model directory {dir} must contain {config.ModelFile} and {config.TokenizerFile}.");
            return (modelPath, tokenizerPath);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        await DownloadIfMissingAsync(http, config.ModelRepo, config.ModelFile, modelPath, ct);
        await DownloadIfMissingAsync(http, config.ModelRepo, config.TokenizerFile, tokenizerPath, ct);
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
