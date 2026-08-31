using System.Diagnostics;
using System.Text.Json;
using MailSearch;
using MailSearch.Config;
using MailSearch.Embeddings;
using MailSearch.Eval;
using MailSearch.Mail;
using MailSearch.Rerank;
using MailSearch.Search;
using MailSearch.Storage;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return await Cli.RunAsync(args, cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or FileNotFoundException or JsonException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static class Cli
{
    private const string Usage = """
        usage: minne [--data-dir <dir>] <command> [options]

        commands:
          config init                 write a default config.json (edit graph.clientId afterwards)
          config show                 print resolved paths and configuration
          login                       sign in to Microsoft 365 (opens a browser)
          logout                      forget the signed-in account
          sync [--full] [--skip-embed] [--folder <name>]...
                                      fetch new/changed mail via Graph delta queries, then embed
          embed [--reset]             embed chunks that have no vector yet (--reset re-embeds everything)
          reindex                     re-run body cleaning + chunking from stored raw bodies, then re-embed
                                      (use after changing cleaning rules or indexing.* settings; no Graph access needed)
          search <query> [--mode hybrid|keyword|vector|rerank] [--top N] [--json] [--ids]
                                      query syntax: words "exact phrase" from:x to:x after:2024-01 before:2024-06 has:attachment folder:inbox
          eval <file> [--top N] [--verbose]
                                      score keyword / vector / hybrid / rerank against a query set
          eval init <file>            write an example evaluation file
          stats                       index statistics

        environment: MINNE_DATA overrides the default data directory (%LOCALAPPDATA%\Minne).
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var list = args.ToList();
        string? dataDir = TakeOption(list, "--data-dir");
        if (list.Count == 0 || list[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var paths = new DataPaths(dataDir);
        var config = AppConfig.Load(paths.ConfigFile);
        var command = list[0];
        var rest = list.Skip(1).ToList();

        return command switch
        {
            "config" => Config(rest, paths, config),
            "login" => await LoginAsync(paths, config, ct),
            "logout" => await LogoutAsync(paths, config),
            "sync" => await SyncAsync(rest, paths, config, ct),
            "embed" => await EmbedAsync(rest, paths, config, ct),
            "reindex" => await ReindexAsync(paths, config, ct),
            "search" => await SearchAsync(rest, paths, config, ct),
            "eval" => await EvalAsync(rest, paths, config, ct),
            "stats" => Stats(paths),
            _ => Fail($"unknown command '{command}'\n\n{Usage}"),
        };
    }

    // ---- commands ----------------------------------------------------------------------

    private static int Config(List<string> args, DataPaths paths, AppConfig config)
    {
        switch (args.FirstOrDefault())
        {
            case "init":
                if (File.Exists(paths.ConfigFile) && !args.Contains("--force"))
                    return Fail($"{paths.ConfigFile} already exists (use --force to overwrite)");
                new AppConfig().Save(paths.ConfigFile);
                Console.WriteLine($"Wrote {paths.ConfigFile}");
                Console.WriteLine("Next: set graph.clientId to your Entra app registration's Application (client) ID, then run 'minne login'.");
                return 0;
            case "show":
            case null:
                Console.WriteLine($"data directory : {paths.Root}");
                Console.WriteLine($"config file    : {paths.ConfigFile} {(File.Exists(paths.ConfigFile) ? "" : "(missing - defaults in use)")}");
                Console.WriteLine($"database       : {paths.DatabaseFile}");
                Console.WriteLine($"model directory: {ModelDownloader.ResolveModelDirectory(config.Embedding.Onnx, paths)}");
                Console.WriteLine();
                // config show output ends up pasted into bug reports; keep the key out of it.
                if (!string.IsNullOrEmpty(config.Embedding.Http.ApiKey))
                    config.Embedding.Http.ApiKey = "***";
                Console.WriteLine(JsonSerializer.Serialize(config, AppConfig.JsonOptions));
                return 0;
            default:
                return Fail("usage: config init|show");
        }
    }

    private static async Task<int> LoginAsync(DataPaths paths, AppConfig config, CancellationToken ct)
    {
        var auth = new GraphAuth(config.Graph, paths);
        await auth.GetAccessTokenAsync(ct);
        Console.WriteLine($"Signed in: {await auth.GetSignedInUserAsync()}");
        return 0;
    }

    private static async Task<int> LogoutAsync(DataPaths paths, AppConfig config)
    {
        await new GraphAuth(config.Graph, paths).SignOutAsync();
        Console.WriteLine("Signed out.");
        return 0;
    }

    private static async Task<int> SyncAsync(List<string> args, DataPaths paths, AppConfig config, CancellationToken ct)
    {
        var full = TakeFlag(args, "--full");
        var skipEmbed = TakeFlag(args, "--skip-embed");
        var folders = new List<string>();
        while (TakeOption(args, "--folder") is { } f) folders.Add(f);
        if (folders.Count == 0) folders = config.Graph.Folders;

        using var store = new SearchStore(paths.DatabaseFile);
        var auth = new GraphAuth(config.Graph, paths);
        var source = new GraphMailSource(auth, config.Graph);
        var indexer = new Indexer(store, config.Indexing);

        var sw = Stopwatch.StartNew();
        foreach (var folder in folders)
        {
            Console.WriteLine($"Syncing '{folder}'{(full ? " (full)" : "")}...");
            var result = await indexer.SyncFolderAsync(source, folder, full,
                p => Console.Write($"\r  {p.Upserted} added/updated, {p.Removed} removed"), ct);
            Console.WriteLine($"\r  {result.Upserted} added/updated, {result.Removed} removed");
        }
        Console.WriteLine($"Sync finished in {sw.Elapsed.TotalSeconds:0.0}s.");

        if (skipEmbed) return 0;
        return await EmbedCoreAsync(store, indexer, paths, config, ct);
    }

    private static async Task<int> EmbedAsync(List<string> args, DataPaths paths, AppConfig config, CancellationToken ct)
    {
        var reset = TakeFlag(args, "--reset");
        using var store = new SearchStore(paths.DatabaseFile);
        if (reset)
        {
            store.ClearEmbeddings();
            Console.WriteLine("Cleared existing embeddings.");
        }
        return await EmbedCoreAsync(store, new Indexer(store, config.Indexing), paths, config, ct);
    }

    private static async Task<int> ReindexAsync(DataPaths paths, AppConfig config, CancellationToken ct)
    {
        using var store = new SearchStore(paths.DatabaseFile);
        var missingRaw = store.CountMessagesWithoutRaw();
        if (missingRaw > 0)
            Console.WriteLine($"note: {missingRaw} messages were synced before raw bodies were kept and will be skipped; run 'sync --full' to refresh them.");
        var indexer = new Indexer(store, config.Indexing);
        Console.WriteLine("Re-cleaning and re-chunking...");
        var sw = Stopwatch.StartNew();
        var count = indexer.ReindexAll(n => Console.Write($"\r  {n} messages"));
        Console.WriteLine($"\r  {count} messages re-indexed in {sw.Elapsed.TotalSeconds:0.0}s.");
        store.SetMeta("embedding_model", null);
        store.SetMeta("embedding_dims", null);
        return await EmbedCoreAsync(store, indexer, paths, config, ct);
    }

    private static async Task<int> EmbedCoreAsync(SearchStore store, Indexer indexer, DataPaths paths, AppConfig config, CancellationToken ct)
    {
        var stats = store.GetStats();
        var pending = stats.Chunks - stats.EmbeddedChunks;
        if (pending == 0)
        {
            Console.WriteLine("All chunks are embedded.");
            return 0;
        }
        Console.WriteLine($"Embedding {pending} chunks...");
        using var provider = await EmbeddingProviderFactory.CreateAsync(config.Embedding, paths, ct);
        Console.WriteLine($"  model: {provider.ModelId} ({provider.Dimensions} dims)");
        var sw = Stopwatch.StartNew();
        var done = await indexer.EmbedPendingAsync(provider, (d, total) =>
        {
            var rate = d / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            var eta = TimeSpan.FromSeconds((total - d) / Math.Max(rate, 0.001));
            Console.Write($"\r  {d}/{total}  {rate:0} chunks/s  eta {eta:mm\\:ss}   ");
        }, ct);
        Console.WriteLine($"\r  embedded {done} chunks in {sw.Elapsed.TotalSeconds:0.0}s.          ");
        return 0;
    }

    private static async Task<int> SearchAsync(List<string> args, DataPaths paths, AppConfig config, CancellationToken ct)
    {
        var mode = ParseMode(TakeOption(args, "--mode"));
        var top = int.TryParse(TakeOption(args, "--top"), out var t) ? t : 10;
        var json = TakeFlag(args, "--json");
        var showIds = TakeFlag(args, "--ids");
        var query = string.Join(' ', args).Trim();
        if (query.Length == 0) return Fail("usage: search <query> [--mode hybrid|keyword|vector|rerank] [--top N] [--json] [--ids]");

        using var store = new SearchStore(paths.DatabaseFile);
        var searcher = new HybridSearcher(store, config.Search,
            c => EmbeddingProviderFactory.CreateAsync(config.Embedding, paths, c),
            c => RerankerFactory.CreateAsync(config.Rerank, paths, c), config.Rerank.Depth);
        var sw = Stopwatch.StartNew();
        var hits = await searcher.SearchAsync(query, mode, top, ct);
        sw.Stop();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(hits.Select(h => new
            {
                h.Rank, h.Score, h.KeywordRank, h.VectorRank, h.Message.Id, h.Message.InternetMessageId, h.Message.Subject,
                From = h.Message.SenderAddress, h.Message.Received, h.Message.Folder, h.Message.HasAttachments, h.Snippet, h.Message.WebLink,
            }), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        if (hits.Count == 0)
        {
            Console.WriteLine("No results.");
            return 0;
        }
        foreach (var h in hits)
        {
            var m = h.Message;
            var from = m.SenderName is { Length: > 0 } ? m.SenderName : m.SenderAddress ?? "?";
            var via = h.KeywordRank is null ? "vec" : h.VectorRank is null ? "kw " : "k+v";
            Console.WriteLine($"{h.Rank,2}. [{via}] {m.Received:yyyy-MM-dd}  {Truncate(from, 24),-24}  {Truncate(m.Subject, 70)}{(m.HasAttachments ? " 📎" : "")}");
            Console.WriteLine($"          {Truncate(h.Snippet, 140)}");
            if (showIds) Console.WriteLine($"          id: {m.InternetMessageId ?? m.Id}   rowid:{m.RowId}");
        }
        Console.WriteLine($"({hits.Count} results, {mode.ToString().ToLowerInvariant()}, {sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    private static async Task<int> EvalAsync(List<string> args, DataPaths paths, AppConfig config, CancellationToken ct)
    {
        if (args.FirstOrDefault() == "init")
        {
            var target = args.ElementAtOrDefault(1) ?? "eval-queries.json";
            new EvalSet
            {
                Description = "Queries you have actually struggled to find. 'expected' takes Internet-Message-Ids, Graph ids or rowid:N (see 'search --ids').",
                Queries =
                [
                    new EvalCase { Query = "kickoff schedule", Expected = ["<put-message-id-here@example.com>"], Note = "email said 'kick-off agenda'" },
                    new EvalCase { Query = "invoice from:contoso after:2025-01", Expected = ["rowid:123"] },
                ],
            }.Save(target);
            Console.WriteLine($"Wrote {target}");
            return 0;
        }

        var verbose = TakeFlag(args, "--verbose");
        var top = int.TryParse(TakeOption(args, "--top"), out var t) ? t : 10;
        var file = args.FirstOrDefault();
        if (file is null || !File.Exists(file)) return Fail("usage: eval <file.json> [--top N] [--verbose]   (create one with 'eval init <file>')");

        var set = EvalSet.Load(file);
        using var store = new SearchStore(paths.DatabaseFile);
        var searcher = new HybridSearcher(store, config.Search,
            c => EmbeddingProviderFactory.CreateAsync(config.Embedding, paths, c),
            c => RerankerFactory.CreateAsync(config.Rerank, paths, c), config.Rerank.Depth);
        var runner = new EvalRunner(searcher, store);
        var results = await runner.RunAsync(set, [SearchMode.Keyword, SearchMode.Vector, SearchMode.Hybrid, SearchMode.Rerank], top, ct);

        var unresolved = results[0].Results.Where(r => r.Unresolvable).ToList();
        Console.WriteLine($"{set.Queries.Count} queries, {results[0].Total} with resolvable expected ids{(unresolved.Count > 0 ? $" ({unresolved.Count} skipped)" : "")}.");
        Console.WriteLine();
        Console.WriteLine($"{"mode",-8} {"R@1",6} {"R@5",6} {$"R@{top}",6} {"MRR",6} {"avg ms",8}");
        foreach (var r in results)
            Console.WriteLine($"{r.Mode.ToString().ToLowerInvariant(),-8} {r.RecallAt(1),6:P0} {r.RecallAt(5),6:P0} {r.RecallAt(top),6:P0} {r.Mrr,6:0.000} {r.AvgMs,8:0}");

        if (verbose || unresolved.Count > 0)
        {
            Console.WriteLine();
            foreach (var u in unresolved) Console.WriteLine($"  ? unresolvable expected id(s) for: {u.Case.Query}");
            if (verbose)
            {
                Console.WriteLine();
                Console.WriteLine($"{"query",-50} {"kw",4} {"vec",4} {"hyb",4} {"rr",4}");
                for (var i = 0; i < set.Queries.Count; i++)
                {
                    if (results[0].Results[i].Unresolvable) continue;
                    string R(int m) => results[m].Results[i].Rank?.ToString() ?? "-";
                    Console.WriteLine($"{Truncate(set.Queries[i].Query, 50),-50} {R(0),4} {R(1),4} {R(2),4} {R(3),4}");
                }
            }
        }
        return 0;
    }

    private static int Stats(DataPaths paths)
    {
        using var store = new SearchStore(paths.DatabaseFile);
        var s = store.GetStats();
        Console.WriteLine($"database        : {paths.DatabaseFile} ({s.DatabaseBytes / 1048576.0:0.0} MB)");
        Console.WriteLine($"messages        : {s.Messages}");
        Console.WriteLine($"chunks          : {s.Chunks} ({s.EmbeddedChunks} embedded)");
        Console.WriteLine($"embedding model : {s.EmbeddingModel ?? "(none yet)"}");
        foreach (var (folder, updated) in store.GetSyncedFolders())
            Console.WriteLine($"folder          : {folder}  (last sync {updated[..19]}Z)");
        return 0;
    }

    // ---- helpers -----------------------------------------------------------------------

    private static SearchMode ParseMode(string? s) => s?.ToLowerInvariant() switch
    {
        null or "hybrid" => SearchMode.Hybrid,
        "keyword" or "kw" or "fts" => SearchMode.Keyword,
        "vector" or "vec" or "semantic" => SearchMode.Vector,
        "rerank" or "rr" => SearchMode.Rerank,
        _ => throw new InvalidOperationException($"unknown mode '{s}' (hybrid|keyword|vector|rerank)"),
    };

    private static string? TakeOption(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        if (i < 0 || i + 1 >= args.Count) return null;
        var value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }

    private static bool TakeFlag(List<string> args, string name) => args.Remove(name);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
