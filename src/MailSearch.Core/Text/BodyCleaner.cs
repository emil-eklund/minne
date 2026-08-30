using System.Text;
using System.Text.RegularExpressions;

namespace MailSearch.Text;

/// <summary>
/// Strips the parts of an email body that hurt retrieval: quoted earlier messages, signatures,
/// disclaimers and inline-image placeholders. Heuristic and multilingual (en/sv/da/no/de/fr/nl/es/it/fi).
/// </summary>
public static partial class BodyCleaner
{
    // "-----Original Message-----", "-----Ursprungligt meddelande-----", "-----Ursprüngliche Nachricht-----" ...
    [GeneratedRegex(@"^\s*-{2,}\s*(Original Message|Ursprungligt meddelande|Ursprüngliche Nachricht|Oprindelig meddelelse|Opprinnelig melding|Alkuperäinen viesti|Message d'origine|Oorspronkelijk bericht|Mensaje original|Messaggio originale|Forwarded message|Vidarebefordrat meddelande|Weitergeleitete Nachricht)\s*-{2,}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorLine();

    // Outlook's horizontal rule (text rendering of the <hr> that precedes quoted content in replies/forwards).
    [GeneratedRegex(@"^[ \t]*_{8,}[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalRule();

    // Header block starting a quoted message: "From: x" followed within a few lines by "Sent:"/"Date:".
    [GeneratedRegex(@"^\s*(From|Från|Fra|Von|De|Van|Lähettäjä|Da)\s*:\s*.+\r?\n(?:.*\r?\n){0,4}?\s*(Sent|Skickat|Sendt|Gesendet|Envoyé|Verzonden|Enviado|Lähetetty|Inviato|Date|Datum|Dato|Päivämäärä|Data)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderBlock();

    // "On Mon, 3 Jun 2024 at 10:12, Anna <anna@x.se> wrote:" / "Den 2024-06-03 skrev Anna:" / "Am 03.06.2024 schrieb Anna:"
    [GeneratedRegex(@"^\s*(On|Den|Am|Le|Op|El|Il)\s.{5,200}?(wrote|skrev|schrieb|a écrit|schreef|escribió|ha scritto|kirjoitti)\s*:\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AttributionLine();

    // Common sign-offs; everything after one of these (if near the end) is treated as a signature.
    [GeneratedRegex(@"^\s*(Best regards|Kind regards|Regards|Best|Thanks|Thank you|Cheers|Sincerely|Med vänliga hälsningar|Med vänlig hälsning|Vänliga hälsningar|Vänligen|Mvh|Hälsningar|Med venlig hilsen|Venlig hilsen|Mvh\.|Vennlig hilsen|Mit freundlichen Grüßen|Viele Grüße|Liebe Grüße|Beste Grüße|Cordialement|Bien à vous|Met vriendelijke groet|Saludos|Un saludo|Cordiali saluti|Ystävällisin terveisin|Terveisin)\s*[,.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SignOffLine();

    // Inline-image placeholders, bare URL brackets, and invisible characters (soft hyphen, zero-width, word joiner, BOM)
    // that HTML newsletters use as spacers.
    [GeneratedRegex(@"\[cid:[^\]]*\]|\[image[^\]]*\]|<https?://[^>\s]+>|\[https?://[^\]\s]+\]|[­​‌‍⁠﻿]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineJunk();

    // Runs of spaces, tabs and non-breaking spaces.
    [GeneratedRegex(@"[ \t ]{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"(\r?\n[ \t ]*){3,}")]
    private static partial Regex MultiNewline();

    public static string Clean(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        var text = body.Replace("\r\n", "\n").Replace('\r', '\n');
        text = InlineJunk().Replace(text, " ");

        text = CutAt(text, HorizontalRule());
        text = CutAt(text, SeparatorLine());
        text = CutAt(text, HeaderBlock());
        text = CutAt(text, AttributionLine());
        text = RemoveQuotedLines(text);
        text = RemoveSignature(text);

        text = MultiSpace().Replace(text, " ");
        text = MultiNewline().Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>
    /// Cut the text at the first match of the pattern. If nothing meaningful precedes the match (e.g. a bare
    /// forward), drop the separator itself and keep what follows instead, so the forwarded content stays indexed.
    /// </summary>
    private static string CutAt(string text, Regex pattern)
    {
        var m = pattern.Match(text);
        if (!m.Success) return text;
        var head = text[..m.Index].TrimEnd();
        if (head.Length >= 20) return head;
        var rest = text[(m.Index + m.Length)..].TrimStart('\n');
        return head.Length == 0 ? rest : head + "\n\n" + rest;
    }

    private static string RemoveQuotedLines(string text)
    {
        if (!text.Contains('>')) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith('>')) continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static string RemoveSignature(string text)
    {
        // Classic "-- " delimiter
        var idx = text.IndexOf("\n-- \n", StringComparison.Ordinal);
        if (idx > 20) text = text[..idx];

        // Sign-off phrase: cut there when what follows is short enough to plausibly be a signature (<= 12 lines).
        var m = SignOffLine().Match(text);
        while (m.Success)
        {
            var tail = text[m.Index..];
            var tailLines = tail.Count(c => c == '\n');
            if (tailLines <= 12 && m.Index >= 20)
            {
                text = text[..m.Index];
                break;
            }
            m = m.NextMatch();
        }
        return text.TrimEnd();
    }
}
