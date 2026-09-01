using Microsoft.ML.Tokenizers;

namespace MailSearch.Embeddings;

/// <summary>
/// Loads a raw SentencePiece model (sentencepiece.bpe.model) through the pure-managed
/// Microsoft.ML.Tokenizers and maps its ids into the fairseq/XLM-R id space the ONNX exports were
/// trained on. Pure managed means every RID works, including ones with no native tokenizer build.
/// </summary>
public sealed class SentencePieceTokenizerAdapter : ITokenizer
{
    // fairseq reserves the first four ids and shifts every sentencepiece piece up by one; the raw
    // sentencepiece <unk> (piece 0) folds onto the fairseq <unk>. XLMRobertaTokenizer does the same.
    private const int BosId = 0, PadId = 1, EosId = 2, UnkId = 3, FairseqOffset = 1;

    /// <summary>The tokens XLM-R's tokenizer.json lists as added tokens, so literal occurrences in the text encode to them.</summary>
    private static readonly (string Token, int Id)[] AddedTokens =
        [("<s>", BosId), ("<pad>", PadId), ("</s>", EosId), ("<unk>", UnkId), ("<mask>", 250001)];

    // Added tokens are registered above the sentencepiece vocabulary so their ids stay
    // distinguishable from ordinary piece ids, which still need the fairseq shift.
    private const int AddedTokenIdBase = 1 << 20;

    private readonly SentencePieceTokenizer _inner;

    public int PadTokenId => PadId;
    public bool DoublePairSeparator { get; }

    public SentencePieceTokenizerAdapter(string modelPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("SentencePiece model not found", modelPath);
        using var stream = File.OpenRead(modelPath);
        // BOS/EOS are appended by Encode in fairseq ids, not by the model in its own raw ids.
        _inner = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false,
            specialTokens: AddedTokens.Select((t, i) => (t.Token, Id: AddedTokenIdBase + i))
                .ToDictionary(t => t.Token, t => t.Id));
        // The model names its own BOS: "<s>" is the RoBERTa/XLM-R marker, "[CLS]" the BERT one.
        DoublePairSeparator = _inner.BeginningOfSentenceToken == "<s>";
    }

    // Microsoft.ML.Tokenizers runs the unigram Viterbi over the whole normalized input where
    // tokenizer.json's WhitespaceSplit pre-tokenizer ran it per word. The two agree until the
    // accumulated scores lose enough precision to break a near-tie differently, measured at
    // 20k+ characters, two orders of magnitude past the ~900-character chunks this app encodes.
    public int[] Encode(string text)
    {
        var pieces = _inner.EncodeToIds(text);
        var ids = new int[pieces.Count + 2];
        ids[0] = BosId;
        for (var i = 0; i < pieces.Count; i++) ids[i + 1] = ToFairseqId(pieces[i]);
        ids[^1] = EosId;
        return ids;
    }

    private static int ToFairseqId(int pieceId) => pieceId switch
    {
        >= AddedTokenIdBase => AddedTokens[pieceId - AddedTokenIdBase].Id,
        0 => UnkId,
        _ => pieceId + FairseqOffset,
    };

    /// <summary>Nothing to release: the model is managed memory reclaimed by the GC.</summary>
    public void Dispose() { }
}
