//using System.Text.RegularExpressions;
//using TradingBot.Models;

//namespace TradingBot.Engines;

//public record SemanticMarketPair(
//    Market A,
//    Market B,
//    double Score
//);

//public class SemanticMarketMatcher
//{
//    private readonly double _minScore;
//    private readonly bool _debug;

//    public SemanticMarketMatcher(double minScore = 0.90, bool debug = false)
//    {
//        _minScore = minScore;
//        _debug = debug;
//    }

//    public List<SemanticMarketPair> FindEquivalentBinaryMarkets(List<Market> markets)
//    {
//        var binaryMarkets = markets
//            .Where(IsBinaryYesNoMarket)
//            .Where(m => !string.IsNullOrWhiteSpace(m.question))
//            .ToList();

//        var result = new List<SemanticMarketPair>();

//        for (int i = 0; i < binaryMarkets.Count; i++)
//        {
//            for (int j = i + 1; j < binaryMarkets.Count; j++)
//            {
//                var a = binaryMarkets[i];
//                var b = binaryMarkets[j];

//                var score = Similarity(a.question, b.question);

//                if (score >= _minScore && HasSameHardConstraints(a.question, b.question))
//                {
//                    result.Add(new SemanticMarketPair(a, b, score));
//                }
//            }
//        }

//        return result
//            .OrderByDescending(x => x.Score)
//            .ToList();
//    }

//    private static bool HasSameHardConstraints(string a, string b)
//    {
//        var constraintsA = ExtractHardConstraints(a);
//        var constraintsB = ExtractHardConstraints(b);

//        if (constraintsA.Count != constraintsB.Count)
//            return false;

//        return constraintsA.SetEquals(constraintsB);
//    }

//    private static HashSet<string> ExtractHardConstraints(string text)
//    {
//        text = text.ToLowerInvariant();

//        var result = new HashSet<string>();

//        // Хваща $6B, $2B, 10 years, 20 years, 5, 10, 2026 и т.н.
//        var numberMatches = Regex.Matches(
//            text,
//            @"\$?\d+(\.\d+)?\s*(k|m|b|bn|million|billion|year|years|days|day|%|percent)?"
//        );

//        foreach (Match match in numberMatches)
//        {
//            var normalized = Regex.Replace(match.Value, @"\s+", "");
//            result.Add($"num:{normalized}");
//        }

//        // Хваща comparison логика
//        if (text.Contains(">") || text.Contains("greater than") || text.Contains("more than") || text.Contains("above"))
//            result.Add("cmp:greater");

//        if (text.Contains("<") || text.Contains("less than") || text.Contains("under") || text.Contains("below"))
//            result.Add("cmp:less");

//        if (text.Contains("between"))
//            result.Add("cmp:between");

//        if (text.Contains("before"))
//            result.Add("time:before");

//        if (text.Contains("after"))
//            result.Add("time:after");

//        return result;
//    }

//    private static bool IsBinaryYesNoMarket(Market market)
//    {
//        if (market.outcomes == null || market.outcomes.Count != 2)
//            return false;

//        var normalized = market.outcomes
//            .Select(x => x.Trim().ToLowerInvariant())
//            .ToList();

//        return normalized.Contains("yes") && normalized.Contains("no");
//    }

//    private static double Similarity(string a, string b)
//    {
//        var tokensA = Tokenize(a);
//        var tokensB = Tokenize(b);

//        if (tokensA.Count == 0 || tokensB.Count == 0)
//            return 0;

//        var intersection = tokensA.Intersect(tokensB).Count();
//        var union = tokensA.Union(tokensB).Count();

//        var jaccard = union == 0 ? 0 : (double)intersection / union;
//        var trigram = TrigramSimilarity(Normalize(a), Normalize(b));

//        return 0.70 * jaccard + 0.30 * trigram;
//    }

//    private static HashSet<string> Tokenize(string text)
//    {
//        var normalized = Normalize(text);

//        var stopWords = new HashSet<string>
//        {
//            "will", "the", "a", "an", "to", "of", "in", "on", "by",
//            "before", "after", "at", "for", "and", "or", "be", "is",
//            "are", "does", "do", "did", "this", "that", "market"
//        };

//        return normalized
//            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
//            .Where(x => x.Length > 2)
//            .Where(x => !stopWords.Contains(x))
//            .ToHashSet();
//    }

//    private static string Normalize(string text)
//    {
//        text = text.ToLowerInvariant();

//        text = Regex.Replace(text, @"[^\w\s]", " ");
//        text = Regex.Replace(text, @"\s+", " ");

//        return text.Trim();
//    }

//    private static double TrigramSimilarity(string a, string b)
//    {
//        var gramsA = GetTrigrams(a);
//        var gramsB = GetTrigrams(b);

//        if (gramsA.Count == 0 || gramsB.Count == 0)
//            return 0;

//        var intersection = gramsA.Intersect(gramsB).Count();
//        var union = gramsA.Union(gramsB).Count();

//        return union == 0 ? 0 : (double)intersection / union;
//    }

//    private static HashSet<string> GetTrigrams(string text)
//    {
//        var result = new HashSet<string>();

//        if (text.Length < 3)
//        {
//            result.Add(text);
//            return result;
//        }

//        for (int i = 0; i <= text.Length - 3; i++)
//            result.Add(text.Substring(i, 3));

//        return result;
//    }
//}

using System.Text.RegularExpressions;
using TradingBot.Models;

namespace TradingBot.Engines;

public record SemanticMarketPair(
    Market A,
    Market B,
    double Score
);

public class SemanticMarketMatcher
{
    private readonly double _minScore;
    private readonly bool _debug;

    public SemanticMarketMatcher(double minScore = 0.90, bool debug = false)
    {
        _minScore = minScore;
        _debug = debug;
    }

    public List<SemanticMarketPair> FindEquivalentBinaryMarkets(List<Market> markets)
    {
        var binaryMarkets = markets
            .Where(IsBinaryYesNoMarket)
            .Where(m => !string.IsNullOrWhiteSpace(m.question))
            .ToList();

        var result = new List<SemanticMarketPair>();
        var nearMisses = new List<(Market A, Market B, double Score, bool HardOk, string Reason)>();

        int comparedPairs = 0;
        int scorePassed = 0;
        int hardPassed = 0;

        for (int i = 0; i < binaryMarkets.Count; i++)
        {
            for (int j = i + 1; j < binaryMarkets.Count; j++)
            {
                comparedPairs++;

                var a = binaryMarkets[i];
                var b = binaryMarkets[j];

                var score = Similarity(a.question, b.question);
                var hardOk = HasCompatibleHardConstraints(a.question, b.question, out var reason);

                if (score >= 0.60)
                {
                    nearMisses.Add((a, b, score, hardOk, reason));
                }

                if (score < _minScore)
                    continue;

                scorePassed++;

                if (!hardOk)
                    continue;

                hardPassed++;

                result.Add(new SemanticMarketPair(a, b, score));
            }
        }

        result = result
            .OrderByDescending(x => x.Score)
            .ToList();

        if (_debug)
        {
            Console.WriteLine();
            Console.WriteLine("========== SEMANTIC MATCHER DEBUG ==========");
            Console.WriteLine($"Total markets: {markets.Count}");
            Console.WriteLine($"Binary YES/NO markets: {binaryMarkets.Count}");
            Console.WriteLine($"Compared pairs: {comparedPairs}");
            Console.WriteLine($"Score passed >= {_minScore:P0}: {scorePassed}");
            Console.WriteLine($"Hard constraints passed: {hardPassed}");
            Console.WriteLine($"Final semantic pairs: {result.Count}");

            Console.WriteLine();
            Console.WriteLine("Top near-misses:");

            foreach (var item in nearMisses
                         .OrderByDescending(x => x.Score)
                         .Take(10))
            {
                Console.WriteLine("--------------------------------------------");
                Console.WriteLine($"Score: {item.Score:P2} | HardOK: {item.HardOk} | Reason: {item.Reason}");
                Console.WriteLine($"A: {item.A.question}");
                Console.WriteLine($"B: {item.B.question}");
            }

            Console.WriteLine("============================================");
            Console.WriteLine();
        }

        return result;
    }

    private static bool IsBinaryYesNoMarket(Market market)
    {
        if (market.outcomes == null || market.outcomes.Count != 2)
            return false;

        var normalized = market.outcomes
            .Select(x => x.Trim().ToLowerInvariant())
            .ToList();

        return normalized.Contains("yes") && normalized.Contains("no");
    }

    private static bool HasCompatibleHardConstraints(
    string a,
    string b,
    out string reason)
    {
        var numbersA = ExtractNumbers(a);
        var numbersB = ExtractNumbers(b);

        if (numbersA.Count > 0 || numbersB.Count > 0)
        {
            if (!numbersA.SetEquals(numbersB))
            {
                reason =
                    $"Different numbers: A=[{string.Join(", ", numbersA)}], " +
                    $"B=[{string.Join(", ", numbersB)}]";
                return false;
            }
        }

        var comparisonsA = ExtractComparisons(a);
        var comparisonsB = ExtractComparisons(b);

        if (comparisonsA.Count > 0 || comparisonsB.Count > 0)
        {
            if (!comparisonsA.SetEquals(comparisonsB))
            {
                reason =
                    $"Different comparisons: A=[{string.Join(", ", comparisonsA)}], " +
                    $"B=[{string.Join(", ", comparisonsB)}]";
                return false;
            }
        }

        var identityA = ExtractIdentityModifiers(a);
        var identityB = ExtractIdentityModifiers(b);

        if (!identityA.SetEquals(identityB))
        {
            reason =
                $"Different identity modifiers: A=[{string.Join(", ", identityA)}], " +
                $"B=[{string.Join(", ", identityB)}]";
            return false;
        }

        var politicalA = ExtractPoliticalOutcomeConstraints(a);
        var politicalB = ExtractPoliticalOutcomeConstraints(b);

        if (!politicalA.SetEquals(politicalB))
        {
            reason =
                $"Different political outcome constraints: A=[{string.Join(", ", politicalA)}], " +
                $"B=[{string.Join(", ", politicalB)}]";
            return false;
        }

        reason = "OK";
        return true;
    }

    private static HashSet<string> ExtractIdentityModifiers(string text)
    {
        text = NormalizeForHardRules(text);

        var result = new HashSet<string>();

        // Donald Trump vs Donald Trump Jr.
        if (Regex.IsMatch(text, @"\b(jr|junior)\b"))
            result.Add("person:jr");

        if (Regex.IsMatch(text, @"\b(sr|senior)\b"))
            result.Add("person:sr");

        if (Regex.IsMatch(text, @"\b(ii|iii|iv)\b"))
            result.Add("person:roman-suffix");

        // EU country vs country
        if (Regex.IsMatch(text, @"\b(eu|european union)\b"))
            result.Add("geo:eu");

        if (Regex.IsMatch(text, @"\b(nato)\b"))
            result.Add("geo:nato");

        if (Regex.IsMatch(text, @"\b(g7)\b"))
            result.Add("geo:g7");

        if (Regex.IsMatch(text, @"\b(g20)\b"))
            result.Add("geo:g20");

        if (Regex.IsMatch(text, @"\b(un|united nations)\b"))
            result.Add("geo:un");

        return result;
    }

    private static HashSet<string> ExtractPoliticalOutcomeConstraints(string text)
    {
        text = NormalizeForHardRules(text);

        var result = new HashSet<string>();

        // Examples:
        // D Senate, D House
        // R Senate, D House
        // Democrat Senate, Republican House

        foreach (Match match in Regex.Matches(
                     text,
                     @"\b(?<party>d|r|dem|democrat|democratic|rep|republican)\s+(?<chamber>senate|house)\b"))
        {
            var party = NormalizeParty(match.Groups["party"].Value);
            var chamber = match.Groups["chamber"].Value;

            result.Add($"control:{chamber}:{party}");
        }

        return result;
    }

    private static string NormalizeParty(string value)
    {
        value = value.ToLowerInvariant();

        return value switch
        {
            "d" or "dem" or "democrat" or "democratic" => "d",
            "r" or "rep" or "republican" => "r",
            _ => value
        };
    }

    private static string NormalizeForHardRules(string text)
    {
        text = text.ToLowerInvariant();
        text = Regex.Replace(text, @"[^\w\s$%<>]", " ");
        text = Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

    private static HashSet<string> ExtractNumbers(string text)
    {
        text = text.ToLowerInvariant();

        var result = new HashSet<string>();

        var matches = Regex.Matches(
            text,
            @"\$?\d+(\.\d+)?\s*(k|m|b|bn|million|billion|year|years|day|days|%|percent)?"
        );

        foreach (Match match in matches)
        {
            var value = match.Value.ToLowerInvariant();

            value = Regex.Replace(value, @"\s+", "");
            value = value.Replace("billion", "b");
            value = value.Replace("million", "m");
            value = value.Replace("bn", "b");
            value = value.Replace("years", "year");
            value = value.Replace("days", "day");
            value = value.Replace("percent", "%");

            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }

        return result;
    }

    private static HashSet<string> ExtractComparisons(string text)
    {
        text = text.ToLowerInvariant();

        var result = new HashSet<string>();

        if (Regex.IsMatch(text, @"(>|greater than|more than|above|over|at least)"))
            result.Add("greater");

        if (Regex.IsMatch(text, @"(<|less than|under|below|fewer than|at most)"))
            result.Add("less");

        if (text.Contains("between"))
            result.Add("between");

        if (text.Contains("before"))
            result.Add("before");

        if (text.Contains("after"))
            result.Add("after");

        return result;
    }

    private static double Similarity(string a, string b)
    {
        var tokensA = Tokenize(a);
        var tokensB = Tokenize(b);

        if (tokensA.Count == 0 || tokensB.Count == 0)
            return 0;

        var intersection = tokensA.Intersect(tokensB).Count();
        var union = tokensA.Union(tokensB).Count();

        var jaccard = union == 0 ? 0 : (double)intersection / union;
        var trigram = TrigramSimilarity(Normalize(a), Normalize(b));

        return 0.70 * jaccard + 0.30 * trigram;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var normalized = Normalize(text);

        var stopWords = new HashSet<string>
        {
            "will", "the", "a", "an", "to", "of", "in", "on", "by",
            "before", "after", "at", "for", "and", "or", "be", "is",
            "are", "does", "do", "did", "this", "that", "market"
        };

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length > 2)
            .Where(x => !stopWords.Contains(x))
            .ToHashSet();
    }

    private static string Normalize(string text)
    {
        text = text.ToLowerInvariant();

        text = Regex.Replace(text, @"[^\w\s$%<>]", " ");
        text = Regex.Replace(text, @"\s+", " ");

        return text.Trim();
    }

    private static double TrigramSimilarity(string a, string b)
    {
        var gramsA = GetTrigrams(a);
        var gramsB = GetTrigrams(b);

        if (gramsA.Count == 0 || gramsB.Count == 0)
            return 0;

        var intersection = gramsA.Intersect(gramsB).Count();
        var union = gramsA.Union(gramsB).Count();

        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> GetTrigrams(string text)
    {
        var result = new HashSet<string>();

        if (text.Length < 3)
        {
            result.Add(text);
            return result;
        }

        for (int i = 0; i <= text.Length - 3; i++)
            result.Add(text.Substring(i, 3));

        return result;
    }
}