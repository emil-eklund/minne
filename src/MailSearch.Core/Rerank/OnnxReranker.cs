using MailSearch.Config;
using MailSearch.Embeddings;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MailSearch.Rerank;

/// <summary>
/// Cross-encoder re-scorer: one forward pass per (query, passage) pair, returning a relevance logit.
/// Works with BERT-style pairs ([CLS] q [SEP] p [SEP]) and RoBERTa/XLM-R-style pairs (&lt;s&gt; q &lt;/s&gt;&lt;/s&gt; p &lt;/s&gt;).
/// </summary>
public sealed class OnnxReranker : IReranker, IUnloadable
{
    private readonly string _modelPath;
    private readonly SessionOptions _options;
    private readonly object _sessionLock = new();
    private InferenceSession? _session;
    private readonly ITokenizer _tokenizer;
    private readonly OnnxRerankConfig _config;
    private readonly Dictionary<string, TensorElementType> _inputs;
    private readonly bool _doubleSeparator;

    public string ModelId { get; }

    public OnnxReranker(string modelPath, ITokenizer tokenizer, OnnxRerankConfig config, string modelId)
    {
        _modelPath = modelPath;
        _tokenizer = tokenizer;
        _config = config;
        ModelId = modelId;
        _doubleSeparator = tokenizer.DoublePairSeparator;

        _options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1),
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        _inputs = Session.InputMetadata.ToDictionary(kv => kv.Key, kv => kv.Value.ElementDataType);
    }

    /// <summary>The live inference session, recreated on demand after <see cref="Unload"/>.</summary>
    private InferenceSession Session
    {
        get { lock (_sessionLock) return _session ??= new InferenceSession(_modelPath, _options); }
    }

    /// <summary>Frees the native session while idle; the next score recreates it from disk. The tokenizer stays loaded (see <see cref="Embeddings.OnnxEmbeddingProvider.Unload"/>).</summary>
    public void Unload()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    public static async Task<OnnxReranker> CreateAsync(OnnxRerankConfig config, DataPaths paths, CancellationToken ct)
    {
        var (modelPath, tokenizerPath) = await ModelDownloader.EnsureAsync(
            config.ModelDirectory, config.ModelRepo, config.ModelFile, config.TokenizerFile, paths, ct);
        var tokenizer = new SentencePieceTokenizerAdapter(tokenizerPath);
        var modelId = config.ModelDirectory is not null
            ? $"local:{Path.GetFileName(config.ModelDirectory)}/{config.ModelFile}"
            : $"{config.ModelRepo}/{config.ModelFile}";
        return new OnnxReranker(modelPath, tokenizer, config, modelId);
    }

    public Task<float[]> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken ct) =>
        Task.Run(() =>
        {
            var scores = new float[passages.Count];
            // leave at least half the token window for the passage
            var queryTokens = Truncate(_tokenizer.Encode(query), Math.Max(_config.MaxTokens / 2, 8));
            for (var offset = 0; offset < passages.Count; offset += _config.BatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var pairs = passages.Skip(offset).Take(_config.BatchSize)
                    .Select(p => BuildPair(queryTokens, _tokenizer.Encode(p))).ToArray();
                var batchScores = Score(pairs, lastInSequence: offset + _config.BatchSize >= passages.Count);
                Array.Copy(batchScores, 0, scores, offset, batchScores.Length);
            }
            return scores;
        }, ct);

    /// <summary>Concatenates the two pre-encoded segments the way the model family expects.</summary>
    private (int[] Ids, int QueryLength) BuildPair(int[] query, int[] passage)
    {
        var separator = query[^1];
        var extra = _doubleSeparator ? 1 : 0;
        var available = _config.MaxTokens - query.Length - extra;

        int[] doc; // the passage without its leading CLS/BOS token, truncated but still ending on its final special token
        if (passage.Length - 1 <= available)
        {
            doc = passage[1..];
        }
        else
        {
            doc = new int[available];
            Array.Copy(passage, 1, doc, 0, available - 1);
            doc[^1] = passage[^1];
        }

        var ids = new int[query.Length + extra + doc.Length];
        query.CopyTo(ids, 0);
        if (extra == 1) ids[query.Length] = separator;
        doc.CopyTo(ids, query.Length + extra);
        return (ids, query.Length + extra);
    }

    private float[] Score((int[] Ids, int QueryLength)[] pairs, bool lastInSequence)
    {
        var n = pairs.Length;
        var maxLen = pairs.Max(p => p.Ids.Length);
        var ids = new long[n * maxLen];
        var mask = new long[n * maxLen];
        var segments = new long[n * maxLen];
        var pad = _tokenizer.PadTokenId;
        for (var i = 0; i < n; i++)
            for (var j = 0; j < maxLen; j++)
            {
                var inRange = j < pairs[i].Ids.Length;
                ids[i * maxLen + j] = inRange ? pairs[i].Ids[j] : pad;
                mask[i * maxLen + j] = inRange ? 1 : 0;
                segments[i * maxLen + j] = inRange && j >= pairs[i].QueryLength ? 1 : 0;
            }

        var shape = new[] { n, maxLen };
        var inputs = new List<NamedOnnxValue>();
        foreach (var (name, type) in _inputs)
        {
            long[] source = name switch
            {
                "input_ids" => ids,
                "attention_mask" => mask,
                // XLM-R-family models have no segment embeddings; if the export still exposes the input it wants zeros
                "token_type_ids" or "segment_ids" => _doubleSeparator ? new long[n * maxLen] : segments,
                _ => throw new NotSupportedException($"Unexpected model input '{name}'."),
            };
            inputs.Add(type == TensorElementType.Int32
                ? NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(source.Select(v => (int)v).ToArray(), shape))
                : NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(source, shape)));
        }

        float[] flat;
        // See OnnxEmbeddingProvider.EmbedBatch: shrink the arena on the final run of a sequence.
        using (var runOptions = new RunOptions())
        {
            if (lastInSequence) runOptions.AddRunConfigEntry("memory.enable_memory_arena_shrinkage", "cpu:0");
            lock (_sessionLock)
            {
                var session = Session;
                using var results = session.Run(inputs, session.OutputMetadata.Keys.ToList(), runOptions);
                flat = results.First().AsTensor<float>().ToArray();
            }
        }
        var perItem = Math.Max(1, flat.Length / n);
        var scores = new float[n];
        for (var i = 0; i < n; i++)
            scores[i] = flat[i * perItem + perItem - 1]; // single relevance logit, or the positive class of a 2-class head
        return scores;
    }

    /// <summary>Keep the first maxTokens-1 tokens and the final (EOS/SEP) token so the sequence still ends properly.</summary>
    private static int[] Truncate(int[] tokens, int maxTokens)
    {
        if (tokens.Length <= maxTokens) return tokens;
        var result = new int[maxTokens];
        Array.Copy(tokens, result, maxTokens - 1);
        result[^1] = tokens[^1];
        return result;
    }

    public void Dispose()
    {
        Unload();
        _options.Dispose();
        _tokenizer.Dispose();
    }
}
