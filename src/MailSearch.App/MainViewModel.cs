using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using MailSearch.Config;
using MailSearch.Embeddings;
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

    public MainViewModel(string? dataDir = null)
    {
        _paths = new DataPaths(dataDir);
        _config = AppConfig.Load(_paths.ConfigFile);
        _store = new SearchStore(_paths.DatabaseFile);
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
        finally { _gate.Release(); }
    }

    // ---- sync ----

    public async Task SyncAsync()
    {
        if (IsSyncing) return;
        if (string.IsNullOrWhiteSpace(_config.Graph.ClientId))
        {
            Status = $"graph.clientId is not set — edit {_paths.ConfigFile} (see README), then restart.";
            return;
        }
        var cts = _syncCts = new CancellationTokenSource();
        IsSyncing = true;
        await _gate.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                var auth = new GraphAuth(_config.Graph, _paths);
                Post("Signing in… (a browser window may open)");
                await auth.GetAccessTokenAsync(cts.Token);
                var source = new GraphMailSource(auth, _config.Graph);
                var indexer = new Indexer(_store, _config.Indexing);
                foreach (var folder in _config.Graph.Folders)
                {
                    Post($"Syncing '{folder}'…");
                    await indexer.SyncFolderAsync(source, folder, full: false,
                        p => Post($"Syncing '{folder}': {p.Upserted} added/updated, {p.Removed} removed"), cts.Token);
                }
                var stats = _store.GetStats();
                var pending = stats.Chunks - stats.EmbeddedChunks;
                if (pending > 0)
                {
                    var provider = await GetProviderAsync(cts.Token);
                    var sw = Stopwatch.StartNew();
                    await indexer.EmbedPendingAsync(provider, (done, total) =>
                        Post($"Embedding {done}/{total} chunks ({done / Math.Max(sw.Elapsed.TotalSeconds, 0.001):0}/s)…"), cts.Token);
                }
            }, cts.Token);
            _searcher = null; // embedding index changed; reload lazily on the next search
            Status = "Sync finished.";
        }
        catch (OperationCanceledException) { Status = "Sync cancelled."; }
        catch (Exception ex) { Status = $"sync error: {ex.Message}"; }
        finally
        {
            _gate.Release();
            IsSyncing = false;
        }
        await RefreshStatsAsync();
    }

    public void CancelSync() => _syncCts?.Cancel();

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
                Status = "The index is empty — press 'Sync mailbox' to fetch your mail (or run 'minne sync').";
        }
        catch (Exception ex)
        {
            StatsText = ex.Message;
        }
    }

    private void Post(string status) => Dispatcher.UIThread.Post(() => Status = status);

    public void Dispose()
    {
        _provider?.Dispose();
        _reranker?.Dispose();
        _store.Dispose();
    }
}
