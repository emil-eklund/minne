using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailSearch.Config;

/// <summary>Root configuration. Stored as config.json inside the data directory.</summary>
public sealed class AppConfig
{
    public GraphConfig Graph { get; set; } = new();
    public EmbeddingConfig Embedding { get; set; } = new();
    public IndexingConfig Indexing { get; set; } = new();
    public SearchConfig Search { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) return new AppConfig();
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AppConfig>(stream, JsonOptions) ?? new AppConfig();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

public sealed class GraphConfig
{
    /// <summary>Application (client) id of your Entra app registration. Required.</summary>
    public string ClientId { get; set; } = "";
    /// <summary>Tenant id, or "common" / "organizations" / "consumers".</summary>
    public string TenantId { get; set; } = "common";
    /// <summary>Well-known folder names (inbox, archive, sentitems, drafts, deleteditems) or folder ids.</summary>
    public List<string> Folders { get; set; } = ["inbox", "archive", "sentitems"];
    /// <summary>Stop the initial sync after roughly this many messages per folder. 0 = no limit.</summary>
    public int MaxMessagesPerFolder { get; set; } = 0;
    public int PageSize { get; set; } = 100;
    /// <summary>Use the device-code flow instead of opening a browser.</summary>
    public bool UseDeviceCode { get; set; } = false;
}

public enum EmbeddingProviderKind { Onnx, Http }

public sealed class EmbeddingConfig
{
    public EmbeddingProviderKind Provider { get; set; } = EmbeddingProviderKind.Onnx;
    public OnnxEmbeddingConfig Onnx { get; set; } = new();
    public HttpEmbeddingConfig Http { get; set; } = new();
}

public enum PoolingMode { Mean, Cls }

/// <summary>
/// Local ONNX sentence-transformer. The default is an EU-developed multilingual model
/// (UKP Lab, TU Darmstadt) covering 50+ languages including all major European ones.
/// Any Hugging Face repo exposing an ONNX export plus tokenizer.json can be used instead,
/// or point <see cref="ModelDirectory"/> at a local folder for fully offline use.
/// </summary>
public sealed class OnnxEmbeddingConfig
{
    public string ModelRepo { get; set; } = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2";
    public string ModelFile { get; set; } = "onnx/model.onnx";
    public string TokenizerFile { get; set; } = "tokenizer.json";
    /// <summary>Optional local directory containing the model and tokenizer files. Overrides downloading.</summary>
    public string? ModelDirectory { get; set; }
    public int MaxTokens { get; set; } = 256;
    public PoolingMode Pooling { get; set; } = PoolingMode.Mean;
    public bool Normalize { get; set; } = true;
    public int BatchSize { get; set; } = 16;
    /// <summary>Prefix prepended to texts being indexed (some models, e.g. e5, expect "passage: ").</summary>
    public string DocumentPrefix { get; set; } = "";
    /// <summary>Prefix prepended to search queries (e.g. "query: " for e5 models).</summary>
    public string QueryPrefix { get; set; } = "";
}

/// <summary>OpenAI-compatible /v1/embeddings endpoint (works with Ollama, LM Studio, vLLM, cloud providers).</summary>
public sealed class HttpEmbeddingConfig
{
    public string Endpoint { get; set; } = "http://localhost:11434/v1/embeddings";
    public string Model { get; set; } = "bge-m3";
    public string? ApiKey { get; set; }
    public int BatchSize { get; set; } = 32;
    public string DocumentPrefix { get; set; } = "";
    public string QueryPrefix { get; set; } = "";
}

public sealed class IndexingConfig
{
    public int ChunkSizeChars { get; set; } = 900;
    public int ChunkOverlapChars { get; set; } = 120;
    /// <summary>Prepend "Subject / From / Date" to every chunk so short chunks carry context.</summary>
    public bool IncludeHeaderInChunk { get; set; } = true;
    /// <summary>Remove quoted replies and signatures before indexing.</summary>
    public bool CleanBodies { get; set; } = true;
}

public sealed class SearchConfig
{
    /// <summary>Candidates fetched from each retriever before fusion.</summary>
    public int CandidateCount { get; set; } = 50;
    /// <summary>Reciprocal-rank-fusion constant. Higher = flatter weighting.</summary>
    public int RrfK { get; set; } = 60;
    /// <summary>Weight of the vector retriever relative to keyword (1.0 = equal).</summary>
    public double VectorWeight { get; set; } = 1.0;
    /// <summary>Multiplier applied to the vector weight when the query contains identifier-like tokens (invoice/ticket numbers, addresses).</summary>
    public double IdentifierVectorWeightFactor { get; set; } = 0.4;
}
