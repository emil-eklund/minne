using MailSearch.Text;

namespace MailSearch.Tests;

public class BodyCleanerTests
{
    [Fact]
    public void Removes_outlook_style_quoted_reply()
    {
        var body = """
            Hi Anna, the kick-off agenda is attached. We start at 09:00 in room B.

            -----Original Message-----
            From: Anna Svensson
            Sent: Monday
            To: Karin
            Subject: RE: kickoff

            Could you send the agenda?
            """;
        var clean = BodyCleaner.Clean(body);
        Assert.Contains("kick-off agenda", clean);
        Assert.DoesNotContain("Could you send", clean);
        Assert.DoesNotContain("Original Message", clean);
    }

    [Fact]
    public void Removes_swedish_header_block()
    {
        var body = "Hej! Här kommer materialet inför kickoffen på fredag, se bilaga.\n\nFrån: Anna Svensson <anna@example.se>\nSkickat: den 3 juni 2024 10:12\nTill: Karin\nÄmne: Kickoff\n\nKan du skicka agendan?";
        var clean = BodyCleaner.Clean(body);
        Assert.Contains("kickoffen", clean);
        Assert.DoesNotContain("Kan du skicka", clean);
    }

    [Fact]
    public void Removes_gmail_style_attribution_and_quoted_lines()
    {
        var body = "Sounds good, see you Friday.\n\nOn Mon, 3 Jun 2024 at 10:12, Anna <anna@example.com> wrote:\n> Are we still on for Friday?\n> Anna";
        var clean = BodyCleaner.Clean(body);
        Assert.Equal("Sounds good, see you Friday.", clean);
    }

    [Fact]
    public void Removes_signature_after_sign_off()
    {
        var body = "The invoice INV-20431 has been paid today.\n\nMed vänliga hälsningar\nJohan Berg\nNordvik AB\n+46 70 000 00 00";
        var clean = BodyCleaner.Clean(body);
        Assert.Equal("The invoice INV-20431 has been paid today.", clean);
    }

    [Fact]
    public void Does_not_cut_when_marker_is_at_the_very_start()
    {
        var body = "Best regards\nThis is actually the whole message with real content that we must keep.";
        var clean = BodyCleaner.Clean(body);
        Assert.Contains("real content", clean);
    }

    [Fact]
    public void Strips_inline_image_placeholders_and_collapses_whitespace()
    {
        var body = "Look at this [cid:image001.png@01DA] chart [https://img.example.com/a.png].\n\n\n\n\nMore   text    here.";
        var clean = BodyCleaner.Clean(body);
        Assert.Equal("Look at this chart .\n\nMore text here.", clean);
    }

    [Fact]
    public void Cuts_at_outlook_horizontal_rule()
    {
        var body = "Yes, approved - go ahead with the order.\n\n________________________________\nFrom: Bob\nSent: Monday\n\nCan we order the licenses?";
        Assert.Equal("Yes, approved - go ahead with the order.", BodyCleaner.Clean(body));
    }

    [Fact]
    public void Bare_forward_keeps_forwarded_content()
    {
        var body = "\n________________________________\nFrom: GitHub <noreply@github.com>\nSent: Monday\nTo: Karin\nSubject: Payment Receipt\n\nYour payment of $4 for the Team plan was received.";
        var clean = BodyCleaner.Clean(body);
        Assert.Contains("payment of $4", clean);
        Assert.DoesNotContain("____", clean);
    }

    [Fact]
    public void Strips_invisible_spacer_characters()
    {
        var body = "­ ­ ­ ­ ­ ­ Receipt from Cordnet​ OÜ.   Total:  40 EUR";
        Assert.Equal("Receipt from Cordnet OÜ. Total: 40 EUR", BodyCleaner.Clean(body));
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal("", BodyCleaner.Clean(""));
        Assert.Equal("", BodyCleaner.Clean("   \n "));
    }
}
