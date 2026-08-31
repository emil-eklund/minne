using System.Globalization;
using System.Text;

namespace MailSearch.Search;

/// <summary>A search query split into free text (for retrieval) and structured filters.</summary>
public sealed class ParsedQuery
{
    public List<string> Terms { get; } = [];
    public List<string> Phrases { get; } = [];
    public string? From { get; set; }
    public string? To { get; set; }
    public DateTimeOffset? After { get; set; }
    public DateTimeOffset? Before { get; set; }
    public bool? HasAttachments { get; set; }
    public string? Folder { get; set; }

    public bool HasFilters => From is not null || To is not null || After is not null || Before is not null || HasAttachments is not null || Folder is not null;
    public bool HasText => Terms.Count > 0 || Phrases.Count > 0;

    /// <summary>Natural-language text for the embedding model.</summary>
    public string SemanticText => string.Join(" ", Phrases.Concat(Terms));

    /// <summary>
    /// FTS5 MATCH expression. Terms are AND-ed; set <paramref name="anyTerm"/> to OR them for a looser fallback.
    /// Stopwords are dropped when at least one content word remains (they still reach the embedding model).
    /// </summary>
    public string ToFtsQuery(bool anyTerm = false)
    {
        var terms = Terms.Where(t => !Stopwords.Contains(t)).ToList();
        if (terms.Count == 0 && Phrases.Count == 0) terms = Terms;
        var parts = Phrases.Concat(terms).Select(Quote).Where(p => p.Length > 2).ToList();
        return string.Join(anyTerm ? " OR " : " AND ", parts);
    }

    /// <summary>Function words in the languages the default model targets (en, sv, da, no, de, nl, fr, es, fi).</summary>
    public static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        // en
        "a","an","the","and","or","of","to","in","on","at","for","is","are","was","were","be","it","this","that","with","from","by","as","we","i","you","he","she","they","my","our","your","when","what","where","who","how","do","does","did","can","will",
        // sv
        "och","att","det","är","en","ett","som","på","av","för","med","till","den","de","om","var","när","vad","vem","hur","jag","du","vi","ni","har","kan","ska","inte","så","nästa","från","eller","min","vår","er",
        // da / no
        "og","er","af","jeg","ikke","hvad","hvornår","når","hva","ikke","fra","eller","til","dette","denne",
        // de
        "der","die","das","und","ist","ich","wir","sie","nicht","mit","für","auf","von","zu","wann","wie","wo","was","ein","eine","oder",
        // nl
        "de","het","een","en","van","is","niet","wat","wanneer","hoe","wij","ik","je","met","voor","naar","of",
        // fr
        "le","la","les","et","est","un","une","des","du","pour","avec","que","qui","quand","où","comment","je","nous","vous","pas","ou",
        // es
        "el","los","las","y","es","una","que","por","con","para","cuándo","cómo","dónde","yo","no","o",
        // fi
        "ja","on","ei","se","että","kun","mitä","milloin","miten","minä","me","tai",
    };

    private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";
}

/// <summary>
/// Supported syntax: free words, "exact phrases", from:name, to:name, after:2024-01-31, before:2024-02,
/// has:attachment, folder:inbox. Filter values may be quoted: from:"Anna Svensson".
/// </summary>
public static class QueryParser
{
    public static ParsedQuery Parse(string input)
    {
        var q = new ParsedQuery();
        foreach (var token in Tokenize(input))
        {
            var colon = token.Text.IndexOf(':');
            if (!token.Quoted && colon > 0 && colon < token.Text.Length - 1
                && ApplyFilter(q, token.Text[..colon].ToLowerInvariant(), token.Text[(colon + 1)..]))
                continue;
            if (token.Quoted) q.Phrases.Add(token.Text);
            else q.Terms.Add(token.Text);
        }
        return q;
    }

    private static bool ApplyFilter(ParsedQuery q, string key, string value)
    {
        switch (key)
        {
            case "from": q.From = value; return true;
            case "to": q.To = value; return true;
            case "folder": case "in": q.Folder = value; return true;
            case "after": case "since":
                if (TryParseDate(value, out var a, out _)) { q.After = a; return true; }
                return false;
            case "before": case "until":
                if (TryParseDate(value, out var b, out _)) { q.Before = b; return true; }
                return false;
            case "has":
                if (value.StartsWith("attach", StringComparison.OrdinalIgnoreCase)) { q.HasAttachments = true; return true; }
                return false;
            default: return false;
        }
    }

    /// <summary>Accepts yyyy, yyyy-MM, yyyy-MM-dd. Returns the start of that period and its (exclusive) end.</summary>
    public static bool TryParseDate(string value, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = end = default;
        foreach (var f in new[] { "yyyy-MM-dd", "yyyy-MM", "yyyy" })
        {
            if (DateTime.TryParseExact(value, f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            {
                start = new DateTimeOffset(dt, TimeSpan.Zero);
                end = f switch { "yyyy" => start.AddYears(1), "yyyy-MM" => start.AddMonths(1), _ => start.AddDays(1) };
                return true;
            }
        }
        return false;
    }

    private readonly record struct Token(string Text, bool Quoted);

    /// <summary>Whitespace tokenizer that keeps "quoted spans" together. Quotes themselves are dropped.</summary>
    private static IEnumerable<Token> Tokenize(string input)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        var startedWithQuote = false;
        foreach (var c in input)
        {
            if (c == '"')
            {
                if (sb.Length == 0 && !inQuotes) startedWithQuote = true;
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    yield return new Token(sb.ToString(), startedWithQuote);
                    sb.Clear();
                    startedWithQuote = false;
                }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return new Token(sb.ToString(), startedWithQuote);
    }
}

public static class QueryHeuristics
{
    /// <summary>
    /// True for tokens that look like identifiers (order numbers, ticket ids, invoice numbers, e-mail addresses,
    /// domains): mixed letters+digits, or containing '@' / a dot-separated domain. Such queries should trust exact
    /// keyword matches more than semantic similarity.
    /// </summary>
    public static bool LooksLikeIdentifier(string term)
    {
        if (term.Length < 3) return false;
        if (term.Contains('@')) return true;
        var hasDigit = false; var hasLetter = false;
        foreach (var c in term)
        {
            if (char.IsDigit(c)) hasDigit = true;
            else if (char.IsLetter(c)) hasLetter = true;
        }
        if (hasDigit && hasLetter) return true;
        if (hasDigit && !hasLetter && term.Length >= 5) return true;          // long pure numbers (order ids)
        var dot = term.IndexOf('.');
        return dot > 0 && dot < term.Length - 2 && !term.EndsWith('.') && term.All(c => char.IsLetterOrDigit(c) || c is '.' or '-'); // domain-like
    }

    /// <summary>
    /// True only when a QUOTED token ("INV-20431") looks like an identifier: quoting is the explicit request
    /// for an exact match. Unquoted identifier-looking words keep the normal keyword/vector balance.
    /// </summary>
    public static bool ContainsIdentifier(ParsedQuery q) => q.Phrases.Any(LooksLikeIdentifier);
}
