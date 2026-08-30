using MailSearch.Search;

namespace MailSearch.Tests;

public class QueryParserTests
{
    [Fact]
    public void Plain_words_become_terms()
    {
        var q = QueryParser.Parse("kickoff schedule");
        Assert.Equal(["kickoff", "schedule"], q.Terms);
        Assert.False(q.HasFilters);
        Assert.Equal("\"kickoff\" AND \"schedule\"", q.ToFtsQuery());
        Assert.Equal("\"kickoff\" OR \"schedule\"", q.ToFtsQuery(anyTerm: true));
    }

    [Fact]
    public void Quoted_text_becomes_phrase()
    {
        var q = QueryParser.Parse("\"kick-off agenda\" friday");
        Assert.Equal(["kick-off agenda"], q.Phrases);
        Assert.Equal(["friday"], q.Terms);
        Assert.Equal("kick-off agenda friday", q.SemanticText);
    }

    [Fact]
    public void Filters_are_extracted()
    {
        var q = QueryParser.Parse("invoice from:contoso to:emil after:2025-01 before:2025-03-15 has:attachment folder:inbox");
        Assert.Equal(["invoice"], q.Terms);
        Assert.Equal("contoso", q.From);
        Assert.Equal("emil", q.To);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), q.After);
        Assert.Equal(new DateTimeOffset(2025, 3, 15, 0, 0, 0, TimeSpan.Zero), q.Before);
        Assert.True(q.HasAttachments);
        Assert.Equal("inbox", q.Folder);
    }

    [Fact]
    public void Quoted_filter_values_keep_spaces()
    {
        var q = QueryParser.Parse("budget from:\"Anna Svensson\"");
        Assert.Equal("Anna Svensson", q.From);
        Assert.Equal(["budget"], q.Terms);
    }

    [Fact]
    public void Unknown_prefix_is_kept_as_term()
    {
        var q = QueryParser.Parse("re:project 10:30");
        Assert.Equal(["re:project", "10:30"], q.Terms);
    }

    [Fact]
    public void Stopwords_are_dropped_from_fts_but_kept_for_semantic_text()
    {
        var q = QueryParser.Parse("när är nästa styrelsemöte");
        Assert.Equal("\"styrelsemöte\"", q.ToFtsQuery());
        Assert.Equal("när är nästa styrelsemöte", q.SemanticText);
    }

    [Fact]
    public void All_stopword_query_still_produces_fts_query()
    {
        var q = QueryParser.Parse("what is this");
        Assert.Equal("\"what\" AND \"is\" AND \"this\"", q.ToFtsQuery());
    }

    [Fact]
    public void Filter_only_query_has_no_text()
    {
        var q = QueryParser.Parse("from:anna has:attachment");
        Assert.False(q.HasText);
        Assert.True(q.HasFilters);
    }

    [Theory]
    [InlineData("2024", "2024-01-01", "2025-01-01")]
    [InlineData("2024-02", "2024-02-01", "2024-03-01")]
    [InlineData("2024-02-29", "2024-02-29", "2024-03-01")]
    public void Dates_parse_to_period(string input, string start, string end)
    {
        Assert.True(QueryParser.TryParseDate(input, out var s, out var e));
        Assert.Equal(DateTimeOffset.Parse(start + "T00:00:00Z"), s);
        Assert.Equal(DateTimeOffset.Parse(end + "T00:00:00Z"), e);
    }
}

public class QueryHeuristicsTests
{
    [Theory]
    [InlineData("SAS13524", true)]
    [InlineData("INV-20431", true)]
    [InlineData("anna@example.se", true)]
    [InlineData("contoso.com", true)]
    [InlineData("4711234", true)]
    [InlineData("travel", false)]
    [InlineData("kick-off", false)]
    [InlineData("Q3", false)]
    [InlineData("2024", false)]
    public void Detects_identifier_like_terms(string term, bool expected) =>
        Assert.Equal(expected, QueryHeuristics.LooksLikeIdentifier(term));

    [Fact]
    public void Query_with_identifier_is_flagged()
    {
        Assert.True(QueryHeuristics.ContainsIdentifier(QueryParser.Parse("invoice INV-20431 status")));
        Assert.False(QueryHeuristics.ContainsIdentifier(QueryParser.Parse("travel expenses policy")));
    }
}
