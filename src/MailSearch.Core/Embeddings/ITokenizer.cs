namespace MailSearch.Embeddings;

public interface ITokenizer : IDisposable
{
    /// <summary>Token ids including any special tokens the model expects (e.g. BOS/EOS).</summary>
    int[] Encode(string text);
    int PadTokenId { get; }
}
