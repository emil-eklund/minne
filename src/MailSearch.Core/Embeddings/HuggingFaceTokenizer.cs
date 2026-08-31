using System.Text.Json;

namespace MailSearch.Embeddings;

/// <summary>Loads any Hugging Face tokenizer.json (WordPiece, BPE, Unigram/SentencePiece) via the native tokenizers library.</summary>
public sealed class HuggingFaceTokenizer : ITokenizer
{
    private readonly Tokenizers.DotNet.Tokenizer _inner;

    public int PadTokenId { get; }

    public HuggingFaceTokenizer(string tokenizerJsonPath)
    {
        if (!File.Exists(tokenizerJsonPath)) throw new FileNotFoundException("tokenizer.json not found", tokenizerJsonPath);
        _inner = new Tokenizers.DotNet.Tokenizer(vocabPath: tokenizerJsonPath);
        PadTokenId = ReadPadTokenId(tokenizerJsonPath);
    }

    public int[] Encode(string text)
    {
        var ids = _inner.Encode(text);
        var result = new int[ids.Length];
        for (var i = 0; i < ids.Length; i++) result[i] = (int)ids[i];
        return result;
    }

    private static int ReadPadTokenId(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("padding", out var padding) && padding.ValueKind == JsonValueKind.Object
                && padding.TryGetProperty("pad_id", out var padId))
                return padId.GetInt32();
            if (root.TryGetProperty("added_tokens", out var added))
                foreach (var t in added.EnumerateArray())
                {
                    var content = t.GetProperty("content").GetString();
                    if (content is "<pad>" or "[PAD]" or "<|pad|>") return t.GetProperty("id").GetInt32();
                }
        }
        catch (JsonException) { }
        return 0;
    }

    public void Dispose() => _inner.Dispose();
}
