using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;
using MailSearch.Config;
using MailSearch.Embeddings;
using MailSearch.Eval;
using MailSearch.Mail;
using MailSearch.Rerank;
using MailSearch.Search;
using MailSearch.Storage;

namespace MailSearch.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed record SnippetRun(string Text, bool Highlight);

/// <summary>A search mode with a user-facing label.</summary>
public sealed record ModeOption(SearchMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One search hit prepared for display.</summary>
public sealed class ResultItem
{
    private static readonly IBrush KwBrush = new SolidColorBrush(Color.Parse("#2F6FDE"));
    private static readonly IBrush VecBrush = new SolidColorBrush(Color.Parse("#2E9E6B"));
    private static readonly IBrush BothBrush = new SolidColorBrush(Color.Parse("#8A56C2"));

    private readonly SearchHit _hit;

    public ResultItem(SearchHit hit)
    {
        _hit = hit;
        SnippetRuns = ParseSnippet(hit.Snippet, marked: hit.KeywordRank is not null);
    }

    public int Rank => _hit.Rank;
    public string Via => _hit.KeywordRank is null ? "similar" : _hit.VectorRank is null ? "exact" : "both";
    public IBrush ViaBrush => _hit.KeywordRank is null ? VecBrush : _hit.VectorRank is null ? KwBrush : BothBrush;
    public string Date => _hit.Message.Received.ToLocalTime().ToString("yyyy-MM-dd");
    public string From => _hit.Message.SenderName is { Length: > 0 } n ? n : _hit.Message.SenderAddress ?? "?";
    public string Subject => string.IsNullOrWhiteSpace(_hit.Message.Subject) ? "(no subject)" : _hit.Message.Subject;
    public string Attachment => _hit.Message.HasAttachments ? "\U0001F4CE" : "";
    public IReadOnlyList<SnippetRun> SnippetRuns { get; }

    // ---- detail pane ----

    public string DetailFrom
    {
        get
        {
            var addr = _hit.Message.SenderAddress ?? "";
            var name = _hit.Message.SenderName ?? "";
            return name.Length > 0 && name != addr ? $"From: {name} <{addr}>" : $"From: {addr}";
        }
    }

    public string DetailTo => _hit.Message.Recipients.Length > 0 ? $"To: {_hit.Message.Recipients}" : "";

    public string DetailMeta =>
        $"{_hit.Message.Received.ToLocalTime():yyyy-MM-dd HH:mm} · {_hit.Message.Folder} · rank {Rank} · found by {ViaLong}";

    private string ViaLong => _hit.KeywordRank is null ? "similar meaning"
        : _hit.VectorRank is null ? "exact words"
        : $"exact words (#{_hit.KeywordRank}) + similar meaning (#{_hit.VectorRank})";

    public string Body => _hit.Message.Body.Length > 0 ? _hit.Message.Body : "(empty body)";
    public bool HasWebLink => !string.IsNullOrEmpty(_hit.Message.WebLink);
    public string? WebLink => _hit.Message.WebLink;
    public string CopyableId => _hit.Message.InternetMessageId ?? _hit.Message.Id;

    /// <summary>FTS5 snippets mark matches with [ and ]; turn them into highlight runs.</summary>
    private static IReadOnlyList<SnippetRun> ParseSnippet(string snippet, bool marked)
    {
        if (!marked || !snippet.Contains('[')) return [new SnippetRun(snippet, false)];
        var runs = new List<SnippetRun>();
        var sb = new StringBuilder();
        var inMatch = false;
        void Flush(bool highlight)
        {
            if (sb.Length == 0) return;
            runs.Add(new SnippetRun(sb.ToString(), highlight));
            sb.Clear();
        }
        foreach (var c in snippet)
        {
            if (c == '[' && !inMatch) { Flush(false); inMatch = true; }
            else if (c == ']' && inMatch) { Flush(true); inMatch = false; }
            else sb.Append(c);
        }
        Flush(inMatch);
        return runs;
    }
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string IdleStatus = "Type a query — filters: from: to: after: before: has:attachment folder: — quote \"INV-1234\" for exact ids";

    private readonly DataPaths _paths;
    private readonly AppConfig _config;
    private readonly SearchStore _store;
    /// <summary>Serializes every SearchStore access (one SQLite connection).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HybridSearcher? _searcher;
    private IEmbeddingProvider? _provider;
    private IReranker? _reranker;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _syncCts;
    private readonly DispatcherTimer? _idleTimer;
    private DateTime _lastUseUtc = DateTime.UtcNow;
    private bool _resourcesLoaded;

    public MainViewModel(string? dataDir = null)
    {
        _paths = new DataPaths(dataDir);
        _config = AppConfig.Load(_paths.ConfigFile);
        // First run: materialize the defaults so there is a file to edit (Tools → Open config file).
        // A failure must not take down startup — the defaults still apply in memory.
        if (!File.Exists(_paths.ConfigFile))
        {
            try { _config.Save(_paths.ConfigFile); }
            catch (Exception ex) { _status = $"Could not write {_paths.ConfigFile}: {ex.Message}"; }
        }
        StatusLog.Sink = Post;
        _store = new SearchStore(_paths.DatabaseFile);
        if (_config.Search.IdleUnloadSeconds > 0)
        {
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _idleTimer.Tick += (_, _) => _ = UnloadIfIdleAsync();
            _idleTimer.Start();
        }
        _ = RefreshStatsAsync();
    }

    // ---- bindable state ----

    private string _queryText = "";
    public string QueryText
    {
        get => _queryText;
        set { if (Set(ref _queryText, value)) QueueSearch(); }
    }

    public ModeOption[] Modes { get; } =
    [
        new(SearchMode.Hybrid, "Hybrid"),
        new(SearchMode.Rerank, "Hybrid + rerank"),
        new(SearchMode.Keyword, "Exact words"),
        new(SearchMode.Vector, "By meaning"),
    ];

    private ModeOption _selectedMode = new(SearchMode.Hybrid, "Hybrid");
    public ModeOption SelectedMode
    {
        get => _selectedMode;
        set { if (Set(ref _selectedMode, value)) QueueSearch(immediate: true); }
    }

    public int[] TopOptions { get; } = [10, 25, 50, 100];

    private int _selectedTop = 10;
    public int SelectedTop
    {
        get => _selectedTop;
        set { if (Set(ref _selectedTop, value)) QueueSearch(immediate: true); }
    }

    public ObservableCollection<ResultItem> Results { get; } = [];

    private ResultItem? _selected;
    public ResultItem? Selected { get => _selected; set => Set(ref _selected, value); }

    private string _status = IdleStatus;
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _statsText = "";
    public string StatsText { get => _statsText; set => Set(ref _statsText, value); }

    private bool _isSyncing;
    public bool IsSyncing { get => _isSyncing; set => Set(ref _isSyncing, value); }

    public string ConfigFilePath => _paths.ConfigFile;
    public string DataDirectory => _paths.Root;

    // ---- searching ----

    private void QueueSearch(bool immediate = false)
    {
        _debounceCts?.Cancel();
        var cts = _debounceCts = new CancellationTokenSource();
        _ = DebouncedSearchAsync(cts.Token, immediate ? 1 : 400);
    }

    private async Task DebouncedSearchAsync(CancellationToken ct, int delayMs)
    {
        try { await Task.Delay(delayMs, ct); }
        catch (OperationCanceledException) { return; }
        if (!ct.IsCancellationRequested) await SearchNowAsync();
    }

    public async Task SearchNowAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var query = QueryText.Trim();
        if (query.Length == 0)
        {
            Results.Clear();
            Selected = null;
            Status = IdleStatus;
            return;
        }

        try { await _gate.WaitAsync(cts.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (cts.IsCancellationRequested) return;
            var searcher = _searcher ??= new HybridSearcher(
                _store, _config.Search, GetProviderAsync, GetRerankerAsync, _config.Rerank.Depth);
            var mode = SelectedMode.Mode;
            var modeLabel = SelectedMode.Label.ToLowerInvariant();
            var top = SelectedTop;
            var sw = Stopwatch.StartNew();
            var hits = await Task.Run(() => searcher.SearchAsync(query, mode, top, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            Results.Clear();
            foreach (var h in hits) Results.Add(new ResultItem(h));
            Selected = Results.FirstOrDefault();
            Status = hits.Count == 0
                ? "No results."
                : $"{hits.Count} results · {modeLabel} · {sw.ElapsedMilliseconds} ms";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = $"error: {ex.Message}"; }
        finally
        {
            _lastUseUtc = DateTime.UtcNow;
            _resourcesLoaded = true;
            _gate.Release();
        }
    }

    /// <summary>
    /// After <see cref="SearchConfig.IdleUnloadSeconds"/> without a search or sync, drops the native
    /// inference sessions and the vector index and trims the working set. The next search reloads
    /// them (a second or two). This is what keeps the app's idle footprint in the tens of MB.
    /// </summary>
    private async Task UnloadIfIdleAsync()
    {
        if (!_resourcesLoaded || IsSyncing) return;
        if ((DateTime.UtcNow - _lastUseUtc).TotalSeconds < _config.Search.IdleUnloadSeconds) return;
        if (!await _gate.WaitAsync(0)) return; // busy right now; the next tick will retry
        try
        {
            _resourcesLoaded = false;
            await Task.Run(() =>
            {
                _searcher?.Unload();
                (_provider as IUnloadable)?.Unload(); // also covers a provider loaded by sync after _searcher was reset
                (_reranker as IUnloadable)?.Unload();
                MemoryReclaimer.Reclaim();
            });
        }
        finally { _gate.Release(); }
    }

    // ---- sync & maintenance ----

    public Task SyncAsync() => RunMaintenanceAsync("sync", async ct =>
    {
        await SyncFoldersAsync(full: false, ct);
        await EmbedPendingAsync(ct);
        return "Sync finished.";
    });

    /// <summary>Re-fetches every folder from scratch (also refreshes messages synced before raw bodies were kept).</summary>
    public Task FullResyncAsync() => RunMaintenanceAsync("resync", async ct =>
    {
        await SyncFoldersAsync(full: true, ct);
        await EmbedPendingAsync(ct);
        return "Full resync finished.";
    });

    /// <summary>Drops every stored vector and re-embeds all chunks — the way to switch embedding models.</summary>
    public Task ReembedAllAsync() => RunMaintenanceAsync("re-embed", async ct =>
    {
        _store.ClearEmbeddings();
        await EmbedPendingAsync(ct);
        return "Re-embed finished.";
    });

    /// <summary>Re-runs body cleaning + chunking from stored raw bodies (after changing indexing settings), then re-embeds.</summary>
    public Task RebuildIndexAsync() => RunMaintenanceAsync("rebuild", async ct =>
    {
        var missingRaw = _store.CountMessagesWithoutRaw();
        var indexer = new Indexer(_store, _config.Indexing);
        Post("Re-cleaning and re-chunking…");
        indexer.ReindexAll(n => Post($"Re-indexed {n} messages…"));
        _store.SetMeta("embedding_model", null);
        _store.SetMeta("embedding_dims", null);
        await EmbedPendingAsync(ct);
        return missingRaw > 0
            ? $"Rebuild finished; {missingRaw} messages synced before raw bodies were kept were skipped — use Tools → Full resync to refresh them."
            : "Rebuild finished.";
    });

    public Task SignOutAsync() => RunMaintenanceAsync("sign out", async _ =>
    {
        await CreateAuth().SignOutAsync();
        return "Signed out — the next sync will ask you to sign in again.";
    });

    public void CancelSync() => _syncCts?.Cancel();

    /// <summary>Runs one exclusive long operation with progress in the status bar; searches wait until it finishes.</summary>
    private async Task RunMaintenanceAsync(string label, Func<CancellationToken, Task<string>> work)
    {
        if (IsSyncing) return;
        var cts = _syncCts = new CancellationTokenSource();
        IsSyncing = true;
        await _gate.WaitAsync();
        try
        {
            Status = await Task.Run(() => work(cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { Status = $"{label} cancelled."; }
        catch (Exception ex) { Status = $"{label} error: {ex.Message}"; }
        finally
        {
            _searcher = null; // the index may have changed; reload lazily on the next search
            _lastUseUtc = DateTime.UtcNow;
            _resourcesLoaded = true;
            _gate.Release();
            IsSyncing = false;
        }
        await RefreshStatsAsync();
    }

    /// <summary>Raised on the UI thread with the full device-code sign-in instructions (URL + code) — too long for the status bar.</summary>
    public event Action<string>? SignInPromptRequested;

    private GraphAuth CreateAuth() => new(_config.Graph, _paths, message =>
    {
        Post(message);
        Dispatcher.UIThread.Post(() => SignInPromptRequested?.Invoke(message));
    });

    private async Task SyncFoldersAsync(bool full, CancellationToken ct)
    {
        var auth = CreateAuth();
        Post("Signing in… (a browser window may open)");
        await auth.GetAccessTokenAsync(ct);
        var source = new GraphMailSource(auth, _config.Graph);
        var indexer = new Indexer(_store, _config.Indexing);
        foreach (var folder in _config.Graph.Folders)
        {
            Post($"Syncing '{folder}'…");
            await indexer.SyncFolderAsync(source, folder, full,
                p => Post($"Syncing '{folder}': {p.Upserted} added/updated, {p.Removed} removed"), ct);
        }
    }

    private async Task EmbedPendingAsync(CancellationToken ct)
    {
        var stats = _store.GetStats();
        if (stats.Chunks - stats.EmbeddedChunks == 0) return;
        var provider = await GetProviderAsync(ct);
        var indexer = new Indexer(_store, _config.Indexing);
        var sw = Stopwatch.StartNew();
        await indexer.EmbedPendingAsync(provider, (done, total) =>
            Post($"Embedding {done}/{total} chunks ({done / Math.Max(sw.Elapsed.TotalSeconds, 0.001):0}/s)…"), ct);
    }

    // ---- evaluation ----

    /// <summary>
    /// Runs an eval set through every retrieval mode and returns a plain-text report. Participates in
    /// the same exclusivity state as sync (IsSyncing, the Cancel button, one operation at a time);
    /// closing the eval window cancels through <paramref name="ct"/>.
    /// </summary>
    public async Task<string> RunEvalAsync(string file, CancellationToken ct)
    {
        const int top = 10;
        if (IsSyncing) return "Another operation is already running — wait for it to finish, then try again.";
        var set = EvalSet.Load(file);
        var cts = _syncCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsSyncing = true;
        try
        {
            await _gate.WaitAsync(cts.Token);
            try
            {
                var searcher = _searcher ??= new HybridSearcher(
                    _store, _config.Search, GetProviderAsync, GetRerankerAsync, _config.Rerank.Depth);
                var runner = new EvalRunner(searcher, _store);
                var results = await Task.Run(() => runner.RunAsync(
                    set, [SearchMode.Keyword, SearchMode.Vector, SearchMode.Hybrid, SearchMode.Rerank], top, cts.Token,
                    (mode, i, n) => Post($"Evaluating {mode.ToString().ToLowerInvariant()} {i}/{n}…")), cts.Token);
                Post("Evaluation finished.");
                return FormatEvalReport(file, set, results, top);
            }
            finally
            {
                _lastUseUtc = DateTime.UtcNow;
                _resourcesLoaded = true;
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Post("Evaluation cancelled.");
            return "Evaluation cancelled.";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    public void CreateExampleEvalSet(string path)
    {
        try
        {
            new EvalSet
            {
                Description = "Queries you have actually struggled to find. 'expected' takes Internet-Message-Ids (Copy Message-Id in the preview pane), Graph ids or rowid:N.",
                Queries =
                [
                    new EvalCase { Query = "kickoff schedule", Expected = ["<put-message-id-here@example.com>"], Note = "email said 'kick-off agenda'" },
                    new EvalCase { Query = "invoice from:contoso after:2025-01", Expected = ["rowid:123"] },
                ],
            }.Save(path);
            Status = $"Wrote {path} — fill it in, then run Tools → Evaluate search quality.";
        }
        catch (Exception ex)
        {
            Status = $"Could not write {path}: {ex.Message}";
        }
    }

    /// <summary>The configuration as JSON with the embedding API key redacted — safe to paste into a bug report.</summary>
    public string GetRedactedConfigJson()
    {
        var clone = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(_config, AppConfig.JsonOptions), AppConfig.JsonOptions)!;
        if (!string.IsNullOrEmpty(clone.Embedding.Http.ApiKey)) clone.Embedding.Http.ApiKey = "***";
        return JsonSerializer.Serialize(clone, AppConfig.JsonOptions);
    }

    private static string FormatEvalReport(string file, EvalSet set, IReadOnlyList<ModeResult> results, int top)
    {
        var sb = new StringBuilder();
        var unresolved = results[0].Results.Where(r => r.Unresolvable).ToList();
        sb.AppendLine(Path.GetFullPath(file));
        sb.AppendLine($"{set.Queries.Count} queries, {results[0].Total} with resolvable expected ids{(unresolved.Count > 0 ? $" ({unresolved.Count} skipped)" : "")}.");
        sb.AppendLine();
        sb.AppendLine($"{"mode",-8} {"R@1",6} {"R@5",6} {$"R@{top}",6} {"MRR",6} {"avg ms",8}");
        foreach (var r in results)
            sb.AppendLine($"{r.Mode.ToString().ToLowerInvariant(),-8} {r.RecallAt(1),6:P0} {r.RecallAt(5),6:P0} {r.RecallAt(top),6:P0} {r.Mrr,6:0.000} {r.AvgMs,8:0}");

        if (unresolved.Count > 0)
        {
            sb.AppendLine();
            foreach (var u in unresolved) sb.AppendLine($"  ? unresolvable expected id(s) for: {u.Case.Query}");
        }

        sb.AppendLine();
        sb.AppendLine($"{"query",-50} {"kw",4} {"vec",4} {"hyb",4} {"rr",4}");
        for (var i = 0; i < set.Queries.Count; i++)
        {
            if (results[0].Results[i].Unresolvable) continue;
            string R(int m) => results[m].Results[i].Rank?.ToString() ?? "-";
            sb.AppendLine($"{Truncate(set.Queries[i].Query, 50),-50} {R(0),4} {R(1),4} {R(2),4} {R(3),4}");
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---- helpers ----

    private async Task<IEmbeddingProvider> GetProviderAsync(CancellationToken ct)
    {
        if (_provider is not null) return _provider;
        Post("Loading embedding model… (first time only)");
        return _provider = await EmbeddingProviderFactory.CreateAsync(_config.Embedding, _paths, ct);
    }

    private async Task<IReranker> GetRerankerAsync(CancellationToken ct)
    {
        if (_reranker is not null) return _reranker;
        Post("Loading reranker model… (first use may download ~450 MB)");
        return _reranker = await RerankerFactory.CreateAsync(_config.Rerank, _paths, ct);
    }

    private async Task RefreshStatsAsync()
    {
        try
        {
            await _gate.WaitAsync();
            StoreStats s;
            try { s = await Task.Run(_store.GetStats); }
            finally { _gate.Release(); }
            var embedded = s.EmbeddedChunks == s.Chunks ? "all embedded" : $"{s.EmbeddedChunks:N0} embedded";
            StatsText = $"{s.Messages:N0} messages · {s.Chunks:N0} chunks ({embedded}) · {s.EmbeddingModel ?? "no model yet"} · {s.DatabaseBytes / 1048576.0:0.0} MB";
            if (s.Messages == 0)
                Status = "The index is empty — press 'Sync mailbox' to fetch your mail (you'll be asked to sign in).";
        }
        catch (Exception ex)
        {
            StatsText = ex.Message;
        }
    }

    private void Post(string status) => Dispatcher.UIThread.Post(() => Status = status);

    public void Dispose()
    {
        if (StatusLog.Sink == (Action<string>)Post) StatusLog.Sink = null; // don't root this instance from the static
        _idleTimer?.Stop();
        _syncCts?.Cancel();
        _provider?.Dispose();
        _reranker?.Dispose();
        _store.Dispose();
    }
}
