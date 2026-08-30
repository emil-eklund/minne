using MailSearch.Search;

namespace MailSearch.Tests;

public class RankFusionTests
{
    [Fact]
    public void Item_in_both_lists_outranks_items_in_one()
    {
        var a = new long[] { 1, 2, 3 };
        var b = new long[] { 3, 4, 5 };
        var fused = RankFusion.ReciprocalRank([(a, 1.0), (b, 1.0)], k: 60);
        Assert.Equal(3, fused[0].Id);
        Assert.Equal(5, fused.Count);
    }

    [Fact]
    public void Weight_scales_contribution()
    {
        var a = new long[] { 1 };
        var b = new long[] { 2 };
        var fused = RankFusion.ReciprocalRank([(a, 1.0), (b, 2.0)]);
        Assert.Equal(2, fused[0].Id);
    }

    [Fact]
    public void Empty_input_gives_empty_output() => Assert.Empty(RankFusion.ReciprocalRank([]));
}
