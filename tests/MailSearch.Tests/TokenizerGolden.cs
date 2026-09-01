using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailSearch.Tests;

/// <summary>
/// Token ids frozen from Hugging Face's own tokenizer.json implementation, so the sentencepiece
/// tokenizer can be checked without the reference implementation (or the ONNX weights) present.
/// </summary>
internal sealed record TokenizerGolden(IReadOnlyList<TokenizerGoldenModel> Models)
{
    public const string FileName = "TokenizerGolden.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static TokenizerGolden Load() => JsonSerializer.Deserialize<TokenizerGolden>(File.ReadAllText(FileName), Json)!;

    public string Serialize() => JsonSerializer.Serialize(this, Json);
}

/// <param name="ModelRepo">Hugging Face repo the ids came from.</param>
/// <param name="MaxTokens">Sequence cap the repo's tokenizer.json applied when the golden ids were captured (the encoders now window instead of truncating). 0 = uncapped.</param>
internal sealed record TokenizerGoldenModel(string ModelRepo, int MaxTokens, int PadTokenId, bool DoublePairSeparator, IReadOnlyList<TokenizerGoldenCase> Cases);

/// <summary>Ids are stored space-separated: one line per case keeps the fixture readable in a diff.</summary>
internal sealed record TokenizerGoldenCase(string Text, string Ids)
{
    public static TokenizerGoldenCase From(string text, int[] ids) => new(text, string.Join(' ', ids));

    [JsonIgnore]
    public int[] TokenIds => Ids.Split(' ').Select(int.Parse).ToArray();
}
