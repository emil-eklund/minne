namespace MailSearch.Embeddings;

public interface ITokenizer : IDisposable
{
    /// <summary>Token ids including any special tokens the model expects (e.g. BOS/EOS).</summary>
    int[] Encode(string text);
    int PadTokenId { get; }
    /// <summary>
    /// True for RoBERTa/XLM-R-family models, whose pair format repeats the separator between the two
    /// segments (&lt;s&gt; q &lt;/s&gt;&lt;/s&gt; p &lt;/s&gt;) and which have no segment embeddings;
    /// false for BERT-style [CLS] q [SEP] p [SEP].
    /// </summary>
    bool DoublePairSeparator { get; }
}
