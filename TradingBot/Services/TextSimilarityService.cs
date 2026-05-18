namespace TradingBot.Services;

public class TextSimilarityService
{
    private readonly HashSet<string> _stopWords = new()
    {
        "will", "the", "a", "an", "be", "to", "in", "of", "and", "on"
    };

    public List<string> Tokenize(string text)
    {
        return text.ToLower()
            .Replace("?", "")
            .Replace(",", "")
            .Split(' ')
            .Where(w => !_stopWords.Contains(w) && w.Length > 2)
            .ToList();
    }

    public double JaccardSimilarity(string a, string b)
    {
        var setA = Tokenize(a).ToHashSet();
        var setB = Tokenize(b).ToHashSet();

        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();

        if (union == 0) return 0;

        return (double)intersection / union;
    }
}