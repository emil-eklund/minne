using System.Numerics.Tensors;
using MailSearch.Config;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MailSearch.Embeddings;

/// <summary>Runs a sentence-transformer style ONNX model on the CPU. Works with any BERT/XLM-R-like encoder export.</summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IUnloadable
{
    private readonly string _modelPath;
    private readonly SessionOptions _options;
    private readonly object _sessionLock = new();
    private InferenceSession? _session;
    private readonly ITokenizer _tokenizer;
    private readonly OnnxEmbeddingConfig _config;
    private readonly string _outputName;
    private readonly Dictionary<string, TensorElementType> _inputs;

    public string ModelId { get; }
    public int Dimensions { get; private set; }
    public int BatchSize => _config.BatchSize;

    public OnnxEmbeddingProvider(string modelPath, ITokenizer tokenizer, OnnxEmbeddingConfig config, string modelId)
    {
        _modelPath = modelPath;
        _tokenizer = tokenizer;
        _config = config;
        ModelId = modelId;

        _options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1),
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        var session = Session;

        _inputs = session.InputMetadata.ToDictionary(kv => kv.Key, kv => kv.Value.ElementDataType);
        // Prefer a token-level output (we pool ourselves); fall back to the first output.
        _outputName = session.OutputMetadata.Keys.FirstOrDefault(n => n is "last_hidden_state" or "token_embeddings")
                      ?? session.OutputMetadata.Keys.First();

        var dims = session.OutputMetadata[_outputName].Dimensions;
        Dimensions = dims.Length > 0 && dims[^1] > 0 ? dims[^1] : 0;
        if (Dimensions == 0) Dimensions = EmbedQueryAsync("dimension probe", CancellationToken.None).GetAwaiter().GetResult().Length;
    }

    /// <summary>The live inference session, recreated on demand after <see cref="Unload"/>.</summary>
    private InferenceSession Session
    {
        get { lock (_sessionLock) return _session ??= new InferenceSession(_modelPath, _options); }
    }

    /// <summary>
    /// Frees the native session (weights + arena) while idle; the next embed recreates it from disk.
    /// The tokenizer stays loaded on purpose: the SentencePiece model is only a few MB of managed
    /// memory, so reloading it from disk on every search would cost more than keeping it.
    /// </summary>
    public void Unload()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    public static async Task<OnnxEmbeddingProvider> CreateAsync(OnnxEmbeddingConfig config, DataPaths paths, CancellationToken ct)
    {
        var (modelPath, tokenizerPath) = await ModelDownloader.EnsureAsync(config, paths, ct);
        var tokenizer = new SentencePieceTokenizerAdapter(tokenizerPath);
        var model = config.ModelDirectory is not null ? $"local:{Path.GetFileName(config.ModelDirectory)}/{config.ModelFile}" : $"{config.ModelRepo}/{config.ModelFile}";
        // The token budget shapes the vectors (window boundaries and context), so it is part of the
        // identity: changing maxTokens trips the same embed --reset guard as swapping the model.
        return new OnnxEmbeddingProvider(modelPath, tokenizer, config, $"{model}@{config.MaxTokens}");
    }

    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        Task.Run(() => Embed(texts.Select(t => _config.DocumentPrefix + t).ToList(), ct), ct);

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken ct) =>
        (await Task.Run(() => Embed([_config.QueryPrefix + text], ct), ct))[0];

    private float[][] Embed(IReadOnlyList<string> texts, CancellationToken ct)
    {
        // Texts over the token budget are embedded as a series of windows whose pooled vectors are
        // averaged weighted by window length. For mean pooling that equals mean-pooling every token
        // (each window vector is sum/length), so nothing past the budget is lost — unlike truncation,
        // which silently dropped it. Each window still fits the model's trained sequence length.
        var windows = new List<(int TextIndex, int[] Tokens)>();
        for (var i = 0; i < texts.Count; i++)
            foreach (var w in SplitIntoWindows(_tokenizer.Encode(texts[i]), _config.MaxTokens))
                windows.Add((i, w));

        var sums = new float[texts.Count][];
        var weights = new float[texts.Count];
        for (var offset = 0; offset < windows.Count; offset += _config.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = windows.Skip(offset).Take(_config.BatchSize).ToList();
            var vectors = EmbedBatch(batch.Select(b => b.Tokens).ToArray(), lastInSequence: offset + _config.BatchSize >= windows.Count);
            for (var i = 0; i < vectors.Length; i++)
            {
                var (textIndex, tokens) = batch[i];
                sums[textIndex] ??= new float[vectors[i].Length];
                TensorPrimitives.MultiplyAdd(vectors[i], tokens.Length, sums[textIndex], sums[textIndex]);
                weights[textIndex] += tokens.Length;
            }
        }

        var result = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            var v = sums[i];
            TensorPrimitives.Divide(v, weights[i], v);
            if (_config.Normalize)
            {
                var norm = TensorPrimitives.Norm(v);
                if (norm > 0) TensorPrimitives.Divide(v, norm, v);
            }
            result[i] = v;
        }
        return result;
    }

    private float[][] EmbedBatch(IReadOnlyList<int[]> encoded, bool lastInSequence)
    {
        var maxLen = Math.Max(1, encoded.Max(e => e.Length));
        var n = encoded.Count;

        var ids = new long[n * maxLen];
        var mask = new long[n * maxLen];
        var pad = _tokenizer.PadTokenId;
        for (var i = 0; i < n; i++)
            for (var j = 0; j < maxLen; j++)
            {
                var inRange = j < encoded[i].Length;
                ids[i * maxLen + j] = inRange ? encoded[i][j] : pad;
                mask[i * maxLen + j] = inRange ? 1 : 0;
            }

        var shape = new[] { n, maxLen };
        var inputs = new List<NamedOnnxValue>();
        foreach (var (name, type) in _inputs)
        {
            long[] source = name switch
            {
                "input_ids" => ids,
                "attention_mask" => mask,
                "token_type_ids" or "segment_ids" => new long[n * maxLen],
                _ => throw new NotSupportedException($"Unexpected model input '{name}'."),
            };
            inputs.Add(type == TensorElementType.Int32
                ? NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(source.Select(v => (int)v).ToArray(), shape))
                : NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(source, shape)));
        }

        float[] flat;
        int[] outDims;
        // The arena otherwise keeps the widest workspace it ever allocated; shrinking on the final
        // run of a sequence returns it to the OS at no cost to the earlier batches.
        using (var runOptions = new RunOptions())
        {
            if (lastInSequence) runOptions.AddRunConfigEntry("memory.enable_memory_arena_shrinkage", "cpu:0");
            lock (_sessionLock)
            {
                using var results = Session.Run(inputs, [_outputName], runOptions);
                var output = results.First().AsTensor<float>();
                outDims = output.Dimensions.ToArray();
                flat = output.ToArray();
            }
        }
        var hidden = outDims[^1];
        var vectors = new float[n][];

        for (var i = 0; i < n; i++)
        {
            var v = new float[hidden];
            if (outDims.Length == 2)
            {
                Array.Copy(flat, i * hidden, v, 0, hidden); // model already pooled
            }
            else if (_config.Pooling == PoolingMode.Cls)
            {
                Array.Copy(flat, i * maxLen * hidden, v, 0, hidden);
            }
            else
            {
                var count = 0;
                for (var j = 0; j < maxLen; j++)
                {
                    if (mask[i * maxLen + j] == 0) continue;
                    var span = flat.AsSpan((i * maxLen + j) * hidden, hidden);
                    TensorPrimitives.Add(v, span, v);
                    count++;
                }
                if (count > 0) TensorPrimitives.Divide(v, count, v);
            }
            vectors[i] = v;
        }
        return vectors;
    }

    /// <summary>
    /// Splits an encoded sequence (BOS ... EOS) into windows of at most <paramref name="maxTokens"/>
    /// tokens, each carrying its own BOS/EOS so every window is a well-formed model input.
    /// A sequence within the budget comes back unchanged as a single window.
    /// </summary>
    public static IEnumerable<int[]> SplitIntoWindows(int[] tokens, int maxTokens)
    {
        if (tokens.Length <= maxTokens)
        {
            yield return tokens;
            yield break;
        }
        var bos = tokens[0];
        var eos = tokens[^1];
        var contentPerWindow = Math.Max(1, maxTokens - 2);
        for (var start = 1; start < tokens.Length - 1; start += contentPerWindow)
        {
            var length = Math.Min(contentPerWindow, tokens.Length - 1 - start);
            var window = new int[length + 2];
            window[0] = bos;
            Array.Copy(tokens, start, window, 1, length);
            window[^1] = eos;
            yield return window;
        }
    }

    public void Dispose()
    {
        Unload();
        _options.Dispose();
        _tokenizer.Dispose();
    }
}
