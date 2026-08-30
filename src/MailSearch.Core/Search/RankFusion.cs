namespace MailSearch.Search;

public static class RankFusion
{
    /// <summary>
    /// Reciprocal rank fusion: score(d) = Σ weight_i / (k + rank_i(d)). Each ranking is an ordered list of ids
    /// (best first). Returns ids ordered by fused score.
    /// </summary>
    public static List<(long Id, double Score)> ReciprocalRank(IEnumerable<(IReadOnlyList<long> Ranking, double Weight)> rankings, int k = 60)
    {
        var scores = new Dictionary<long, double>();
        foreach (var (ranking, weight) in rankings)
            for (var i = 0; i < ranking.Count; i++)
                scores[ranking[i]] = scores.GetValueOrDefault(ranking[i]) + weight / (k + i + 1);
        return scores.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
