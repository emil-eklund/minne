namespace MailSearch.Tests;

/// <summary>The inputs the tokenizer implementations must agree on, byte for byte.</summary>
internal static class TokenizerEquivalenceCases
{
    public static readonly string[] All =
    [
        "",
        " ",
        "   \t\t\n\n  \r\n ",
        "The quarterly report is attached, please review before Friday.",
        "Vi behöver stämma av årsredovisningen på måndag i Växjö.",
        "Grüße aus Düsseldorf — die Straße ist wegen Bauarbeiten gesperrt, Maß für Maß.",
        "Veuillez trouver ci-joint la facture équivalente à l'année dernière.",
        "请查收附件中的季度报告，谢谢。",
        "会議は明日の午前十時からです。",
        "Deadline moved 🎉 — see you at the 🇸🇪 office 👨‍👩‍👧‍👦 𝕏 𓀀",
        "<s> and </s> and <pad> and <mask> literally in the text",
        "Reference INV-20431 relates to shipment SAS13524 and order #A/778-902.",
        "emil.eklund@aimplan.com wrote to no-reply+tag@sub.example.co.uk",
        "https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/tokenizer.json?x=1#frag",
        "Möte om INV-20431 med 王小明 and Владимир, see https://example.com/ä",
        "Ελληνικά, עברית, العربية, ไทย, हिन्दी mixed in one line",
        "\u0041\u030A vs \u00C5 vs \uFF21\uFF22\uFF23 vs \u00BD vs \u2460",
        LongDocument,
    ];

    /// <summary>Mirrors the encoders' own truncation: keep the first maxTokens-1 ids and the final EOS. 0 = no cap.</summary>
    public static int[] Truncate(int[] ids, int maxTokens)
    {
        if (maxTokens == 0 || ids.Length <= maxTokens) return ids;
        var result = new int[maxTokens];
        Array.Copy(ids, result, maxTokens - 1);
        result[^1] = ids[^1];
        return result;
    }

    /// <summary>Longer than any chunk the indexer produces, so the encoders' truncation paths run.</summary>
    public static string LongDocument =>
        string.Join(" ", Enumerable.Range(0, 50).Select(i =>
            $"Ärende {i} INV-{20000 + i} förfaller {i % 28 + 1}/3 — kontakta support{i}@example.com för 详情."));
}
