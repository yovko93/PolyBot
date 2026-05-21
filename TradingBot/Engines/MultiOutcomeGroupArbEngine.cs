using System.Text.RegularExpressions;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Engines;

public class MultiOutcomeGroupArbEngine
{
    private const string NoBasketStrategy = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE";
    private const string YesBasketStrategy = "BUY_ALL_YES_COMPLETE_SET";

    private readonly IOrderBookProvider _orderBooks;
    private readonly decimal _minEdgePerShare;
    private readonly decimal _feeBufferPerLeg;
    private readonly decimal _slippageBufferPerLeg;
    private readonly bool _enableYesBasket;

    private readonly OpportunityMonitor? _monitor;
    private readonly RequoteExecutionGate? _requoteGate;
    private readonly ExecutionDecisionService? _decisionService;

    // Used only by requote gate. The main execution decision still belongs to PaperTradingEngine.
    private readonly decimal _requoteMinExpectedProfit;

    public MultiOutcomeGroupArbEngine(
        IOrderBookProvider orderBooks,
        decimal minEdgePerShare = 0.005m,
        decimal feeBufferPerLeg = 0.001m,
        decimal slippageBufferPerLeg = 0.001m,
        bool enableYesBasket = false,
        OpportunityMonitor? monitor = null,
        RequoteExecutionGate? requoteGate = null,
        ExecutionDecisionService? decisionService = null,
        decimal requoteMinExpectedProfit = 0.25m)
    {
        _orderBooks = orderBooks;
        _minEdgePerShare = minEdgePerShare;
        _feeBufferPerLeg = feeBufferPerLeg;
        _slippageBufferPerLeg = slippageBufferPerLeg;
        _enableYesBasket = enableYesBasket;
        _monitor = monitor;
        _requoteGate = requoteGate;
        _decisionService = decisionService;
        _requoteMinExpectedProfit = requoteMinExpectedProfit;
    }

    public async Task ScanAsync(
        List<Market> markets,
        PaperTradingEngine paper,
        SemaphoreSlim semaphore,
        CancellationToken ct = default)
    {
        var groupedMarkets = BuildGroups(markets);

        var tasks = groupedMarkets.Select(group =>
            ScanGroupAsync(
                group.Select(x => (x.Market, x.Group!)).ToList(),
                paper,
                semaphore,
                ct
            )
        );

        var results = await Task.WhenAll(tasks);

        PrintSummary(results);
    }

    private static List<IGrouping<string, GroupedMarket>> BuildGroups(List<Market> markets)
    {
        return markets
            .Where(m => m != null && !string.IsNullOrWhiteSpace(m.question))
            .Select(m => new GroupedMarket(
                Market: m,
                Group: ExtractGroup(m.question)
            ))
            .Where(x => x.Group != null)
            .Select(x => new GroupedMarket(x.Market, x.Group!))
            .GroupBy(x => x.Group.GroupKey)
            .Where(g => g.Count() >= 2)
            .ToList();
    }

    private async Task<GroupScanResult> ScanGroupAsync(
        List<(Market Market, OutcomeGroupInfo Group)> groupMarkets,
        PaperTradingEngine paper,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        var groupKey = groupMarkets.First().Group.GroupKey;

        var snapshots = await LoadSnapshotsAsync(groupMarkets, semaphore, ct);

        if (snapshots.Count < 2)
            return GroupScanResult.Empty(groupKey, snapshots.Count);

        var noResult = await EvaluateNoBasketAsync(
            groupKey,
            snapshots,
            paper,
            ct
        );

        var yesResult = _enableYesBasket
            ? EvaluateYesBasket(groupKey, snapshots, paper)
            : BasketResult.Empty;

        return new GroupScanResult(
            GroupKey: groupKey,
            MarketsInGroup: snapshots.Count,
            NoBasketCandidate: noResult.Candidate,
            NoBasketExecuted: noResult.Executed,
            NoBasketCost: noResult.Cost,
            NoBasketGuaranteedPayout: noResult.GuaranteedPayout,
            YesBasketCandidate: yesResult.Candidate,
            YesBasketExecuted: yesResult.Executed,
            YesBasketCost: yesResult.Cost
        );
    }

    private async Task<List<SnapshotItem>> LoadSnapshotsAsync(
        List<(Market Market, OutcomeGroupInfo Group)> groupMarkets,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        var result = new List<SnapshotItem>();

        foreach (var item in groupMarkets)
        {
            await semaphore.WaitAsync(ct);

            try
            {
                var snapshot = await _orderBooks.GetBinarySnapshotAsync(item.Market, ct);

                if (snapshot != null)
                {
                    result.Add(new SnapshotItem(
                        Market: item.Market,
                        Group: item.Group,
                        Snapshot: snapshot
                    ));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MULTI-OUTCOME SNAPSHOT ERROR] {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        }

        return result;
    }

    private async Task<BasketResult> EvaluateNoBasketAsync(
        string groupKey,
        List<SnapshotItem> snapshots,
        PaperTradingEngine paper,
        CancellationToken ct)
    {
        var opportunity = BuildNoBasketOpportunity(groupKey, snapshots);

        if (opportunity == null)
            return BasketResult.Empty;

        RecordMonitorPreview(
            opportunity,
            paper,
            engine: "MultiOutcomeGroup"
        );

        if (opportunity.EdgePerShare < _minEdgePerShare)
        {
            return BasketResult.FromOpportunity(
                opportunity,
                candidate: false,
                executed: false
            );
        }

        if (opportunity.Quantity <= 0)
        {
            return BasketResult.FromOpportunity(
                opportunity,
                candidate: true,
                executed: false
            );
        }

        if (_requoteGate != null)
        {
            var sourceMarkets = snapshots
                .Select(x => x.Market)
                .ToList();

            var stillValid = await _requoteGate.ValidateBasketNoAsync(
                opportunity,
                sourceMarkets,
                _minEdgePerShare,
                _requoteMinExpectedProfit,
                _feeBufferPerLeg,
                _slippageBufferPerLeg,
                ct
            );

            if (!stillValid)
            {
                Console.WriteLine($"[REQUOTE SKIP] Basket arb failed depth validation. Group={groupKey}");

                return BasketResult.FromOpportunity(
                    opportunity,
                    candidate: true,
                    executed: false
                );
            }
        }

        var executed = paper.RecordBasketArbitrage(opportunity);

        if (executed)
            PrintExecutedBasket("MULTI-OUTCOME NO BASKET ARB", opportunity);

        return BasketResult.FromOpportunity(
            opportunity,
            candidate: true,
            executed: executed
        );
    }

    private BasketResult EvaluateYesBasket(
        string groupKey,
        List<SnapshotItem> snapshots,
        PaperTradingEngine paper)
    {
        // ВАЖНО:
        // YES basket е валиден само ако групата съдържа всички възможни outcomes.
        // Затова default-но engine-ът го държи изключен.

        var opportunity = BuildYesBasketOpportunity(groupKey, snapshots);

        if (opportunity == null)
            return BasketResult.Empty;

        RecordMonitorPreview(
            opportunity,
            paper,
            engine: "MultiOutcomeGroup"
        );

        if (opportunity.EdgePerShare < _minEdgePerShare)
        {
            return BasketResult.FromOpportunity(
                opportunity,
                candidate: false,
                executed: false
            );
        }

        if (opportunity.Quantity <= 0)
        {
            return BasketResult.FromOpportunity(
                opportunity,
                candidate: true,
                executed: false
            );
        }

        var executed = paper.RecordBasketArbitrage(opportunity);

        if (executed)
            PrintExecutedBasket("MULTI-OUTCOME YES BASKET ARB", opportunity);

        return BasketResult.FromOpportunity(
            opportunity,
            candidate: true,
            executed: executed
        );
    }

    private BasketArbOpportunity? BuildNoBasketOpportunity(
        string groupKey,
        List<SnapshotItem> snapshots)
    {
        var legs = new List<BasketArbLeg>();

        foreach (var item in snapshots)
        {
            var noAsk = item.Snapshot.NoAsk;

            if (noAsk == null)
                return null;

            // todo use this
            //legs.Add(new BasketArbLeg(
            //    MarketId: item.Snapshot.MarketId,
            //    TokenId: item.Snapshot.NoTokenId,
            //    Question: item.Snapshot.Question,
            //    Outcome: "NO",
            //    Price: noAsk.Price,
            //    Size: noAsk.Size
            //));

            legs.Add(new BasketArbLeg(
                MarketId: item.Snapshot.MarketId,
                TokenId: item.Snapshot.MarketId,
                Question: item.Snapshot.Question,
                Outcome: "NO",
                Price: noAsk.Price,
                Size: noAsk.Size
            ));
        }

        if (legs.Count < 2)
            return null;

        var rawCost = legs.Sum(x => x.Price);
        var adjustedCost = AddPerLegBuffers(rawCost, legs.Count);

        var guaranteedPayout = legs.Count - 1m;
        var edge = guaranteedPayout - adjustedCost;
        var quantity = legs.Min(x => x.Size);

        return new BasketArbOpportunity(
            GroupKey: groupKey,
            Strategy: NoBasketStrategy,
            Legs: legs,
            Quantity: quantity,
            CostPerShare: adjustedCost,
            GuaranteedPayoutPerShare: guaranteedPayout,
            EdgePerShare: edge,
            ExpectedProfit: quantity * edge
        );
    }

    private BasketArbOpportunity? BuildYesBasketOpportunity(
        string groupKey,
        List<SnapshotItem> snapshots)
    {
        var legs = new List<BasketArbLeg>();

        foreach (var item in snapshots)
        {
            var yesAsk = item.Snapshot.YesAsk;

            if (yesAsk == null)
                return null;

            legs.Add(new BasketArbLeg(
                 MarketId: item.Snapshot.MarketId,
                 TokenId: item.Snapshot.YesTokenId,
                 Question: item.Snapshot.Question,
                 Outcome: "YES",
                 Price: yesAsk.Price,
                 Size: yesAsk.Size
             ));
        }

        if (legs.Count < 2)
            return null;

        var rawCost = legs.Sum(x => x.Price);
        var adjustedCost = AddPerLegBuffers(rawCost, legs.Count);

        var guaranteedPayout = 1m;
        var edge = guaranteedPayout - adjustedCost;
        var quantity = legs.Min(x => x.Size);

        return new BasketArbOpportunity(
            GroupKey: groupKey,
            Strategy: YesBasketStrategy,
            Legs: legs,
            Quantity: quantity,
            CostPerShare: adjustedCost,
            GuaranteedPayoutPerShare: guaranteedPayout,
            EdgePerShare: edge,
            ExpectedProfit: quantity * edge
        );
    }

    private decimal AddPerLegBuffers(decimal rawCost, int legsCount)
    {
        return rawCost + legsCount * (_feeBufferPerLeg + _slippageBufferPerLeg);
    }

    private void RecordMonitorPreview(
    BasketArbOpportunity opportunity,
    PaperTradingEngine paper,
    string engine)
    {
        var monitorQuantity = opportunity.Quantity;
        var monitorExpectedProfit = opportunity.ExpectedProfit;
        var monitorExecutable =
            opportunity.EdgePerShare >= _minEdgePerShare &&
            opportunity.Quantity > 0;

        if (_decisionService != null)
        {
            var decision = _decisionService.EvaluateBasket(
                opportunity,
                currentBalance: paper.Balance,
                currentLockedCapital: paper.LockedCapital
            );

            monitorQuantity = decision.ExecutableQuantity;
            monitorExpectedProfit = decision.ExpectedProfit;
            monitorExecutable = decision.CanExecute;
        }

        var orderLegs = opportunity.Legs.Select(leg => new OrderLegCandidate(
            Strategy: opportunity.Strategy,
            GroupKey: opportunity.GroupKey,
            Question: leg.Question,
            TokenId: leg.TokenId,
            Outcome: leg.Outcome,
            Side: LiveOrderSide.BUY,
            Price: leg.Price,
            Size: monitorQuantity,
            EdgePerShare: opportunity.EdgePerShare
        )).ToList();

        _monitor?.Record(new ArbMonitorRecord(
            TimestampUtc: DateTime.UtcNow,
            Engine: engine,
            Strategy: opportunity.Strategy,
            Key: opportunity.GroupKey,
            EdgePerShare: opportunity.EdgePerShare,
            CostOrProceeds: opportunity.CostPerShare,
            GuaranteedPayout: opportunity.GuaranteedPayoutPerShare,
            QuantityAvailable: monitorQuantity,
            ExpectedProfit: monitorExpectedProfit,
            IsExecutable: monitorExecutable,
            Leg1: $"{opportunity.Strategy} | Legs={opportunity.Legs.Count}",
            Leg2: SummarizeBasketLegs(opportunity.Legs),
            GroupKey: opportunity.GroupKey
        )
        {
            OrderLegs = orderLegs
        });
    }

    private static void PrintExecutedBasket(
        string title,
        BasketArbOpportunity opportunity)
    {
        Console.WriteLine();
        Console.WriteLine($"========== {title} ==========");
        Console.WriteLine($"Group: {opportunity.GroupKey}");
        Console.WriteLine($"Strategy: {opportunity.Strategy}");
        Console.WriteLine($"Legs: {opportunity.Legs.Count}");
        Console.WriteLine($"Adjusted cost/share: {opportunity.CostPerShare}");
        Console.WriteLine($"Guaranteed payout/share: {opportunity.GuaranteedPayoutPerShare}");
        Console.WriteLine($"Edge/share: {opportunity.EdgePerShare}");
        Console.WriteLine($"Quantity available: {opportunity.Quantity}");

        foreach (var leg in opportunity.Legs.OrderBy(x => x.Price))
        {
            Console.WriteLine($"{leg.Outcome} @ {leg.Price} | Size {leg.Size} | {leg.Question}");
        }

        Console.WriteLine("=================================================");
        Console.WriteLine();
    }

    private static void PrintSummary(GroupScanResult[] results)
    {
        var scannedGroups = results.Length;
        var totalMarketsInGroups = results.Sum(x => x.MarketsInGroup);

        var noCandidates = results.Count(x => x.NoBasketCandidate);
        var noExecuted = results.Count(x => x.NoBasketExecuted);

        var yesCandidates = results.Count(x => x.YesBasketCandidate);
        var yesExecuted = results.Count(x => x.YesBasketExecuted);

        var topNo = results
            .Where(x => x.NoBasketCost.HasValue && x.NoBasketGuaranteedPayout.HasValue)
            .OrderByDescending(x => x.NoBasketGuaranteedPayout!.Value - x.NoBasketCost!.Value)
            .Take(10)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("========== MULTI-OUTCOME GROUP SCAN ==========");
        Console.WriteLine($"Groups found: {scannedGroups}");
        Console.WriteLine($"Markets in groups: {totalMarketsInGroups}");
        Console.WriteLine($"NO basket candidates: {noCandidates}");
        Console.WriteLine($"NO basket executed: {noExecuted}");

        if (yesCandidates > 0 || yesExecuted > 0)
        {
            Console.WriteLine($"YES basket candidates: {yesCandidates}");
            Console.WriteLine($"YES basket executed: {yesExecuted}");
        }

        //if (topNo.Count > 0)
        //{
        //    Console.WriteLine();
        //    Console.WriteLine("Top closest NO baskets:");

        //    foreach (var item in topNo)
        //    {
        //        var edge = item.NoBasketGuaranteedPayout!.Value - item.NoBasketCost!.Value;

        //        Console.WriteLine("----------------------------------------");
        //        Console.WriteLine($"Group: {item.GroupKey}");
        //        Console.WriteLine($"Outcomes: {item.MarketsInGroup}");
        //        Console.WriteLine($"NO cost: {item.NoBasketCost.Value:0.####}");
        //        Console.WriteLine($"Guaranteed payout: {item.NoBasketGuaranteedPayout.Value:0.####}");
        //        Console.WriteLine($"Edge: {edge:0.####}");
        //    }
        //}

        Console.WriteLine("==============================================");
        Console.WriteLine();
    }

    private static OutcomeGroupInfo? ExtractGroup(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return null;

        var normalized = NormalizeQuestion(question);

        var winMatch = Regex.Match(
            normalized,
            @"^will\s+(the\s+)?(?<outcome>.+?)\s+win\s+(the\s+)?(?<event>.+?)\??$",
            RegexOptions.IgnoreCase
        );

        if (winMatch.Success)
        {
            var outcome = CleanOutcome(winMatch.Groups["outcome"].Value);
            var eventName = CleanEvent(winMatch.Groups["event"].Value);

            if (string.IsNullOrWhiteSpace(outcome) || string.IsNullOrWhiteSpace(eventName))
                return null;

            var outcomeKind = ClassifyOutcomeKind(outcome, eventName);

            return new OutcomeGroupInfo(
                GroupKey: $"winner:{eventName}|kind:{outcomeKind}",
                OutcomeName: outcome,
                OutcomeKind: outcomeKind
            );
        }

        var colonMatch = Regex.Match(
            normalized,
            @"^(?<event>.+?):\s*(?<outcome>.+)$",
            RegexOptions.IgnoreCase
        );

        if (colonMatch.Success)
        {
            var eventName = CleanEvent(colonMatch.Groups["event"].Value);
            var outcome = CleanOutcome(colonMatch.Groups["outcome"].Value);

            if (string.IsNullOrWhiteSpace(outcome) || string.IsNullOrWhiteSpace(eventName))
                return null;

            var outcomeKind = ClassifyOutcomeKind(outcome, eventName);

            return new OutcomeGroupInfo(
                GroupKey: $"colon-event:{eventName}|kind:{outcomeKind}",
                OutcomeName: outcome,
                OutcomeKind: outcomeKind
            );
        }

        return null;
    }

    private static string ClassifyOutcomeKind(string outcome, string eventName)
    {
        outcome = outcome.ToLowerInvariant().Trim();
        eventName = eventName.ToLowerInvariant().Trim();

        if (IsPartyOutcome(outcome))
            return "party";

        if (IsPoliticalControlOutcome(outcome))
            return "political-control";

        if (LooksLikePersonOutcome(outcome, eventName))
            return "person";

        return "generic";
    }

    private static bool IsPartyOutcome(string outcome)
    {
        var parties = new HashSet<string>
        {
            "democrat",
            "democrats",
            "democratic",
            "democratic party",
            "republican",
            "republicans",
            "republican party",
            "gop",
            "libertarian",
            "libertarians",
            "libertarian party",
            "green",
            "green party",
            "independent",
            "independents",
            "third party",
            "other party"
        };

        if (parties.Contains(outcome))
            return true;

        return Regex.IsMatch(
            outcome,
            @"\b(democrats|republicans|democratic party|republican party|gop)\b"
        );
    }

    private static bool IsPoliticalControlOutcome(string outcome)
    {
        return Regex.IsMatch(
            outcome,
            @"\b(d|r|dem|democrat|democratic|rep|republican)\s+(senate|house)\b"
        );
    }

    private static bool LooksLikePersonOutcome(string outcome, string eventName)
    {
        if (!Regex.IsMatch(
                eventName,
                @"\b(election|nomination|presidential|senate race|governor race|mayor)\b"))
        {
            return false;
        }

        return !IsPartyOutcome(outcome) &&
               !IsPoliticalControlOutcome(outcome) &&
               outcome != "other";
    }

    private static string NormalizeQuestion(string question)
    {
        question = question.ToLowerInvariant();
        question = Regex.Replace(question, @"\s+", " ");
        return question.Trim();
    }

    private static string CleanOutcome(string value)
    {
        value = value.ToLowerInvariant();
        value = Regex.Replace(value, @"[^\w\s]", " ");
        value = Regex.Replace(value, @"\s+", " ");
        return value.Trim();
    }

    private static string CleanEvent(string value)
    {
        value = value.ToLowerInvariant();
        value = value.Trim().Trim('?');

        value = Regex.Replace(value, @"[^\w\s]", " ");
        value = Regex.Replace(value, @"\s+", " ");

        value = Regex.Replace(value, @"\bthe\b", " ");
        value = Regex.Replace(value, @"\s+", " ");

        return value.Trim();
    }

    private static string SummarizeBasketLegs(
        List<BasketArbLeg> legs,
        int maxLegs = 12)
    {
        var shown = legs
            .OrderBy(x => x.Price)
            .Take(maxLegs)
            .Select(x => $"{x.Outcome} @ {x.Price} | {x.Question}");

        var summary = string.Join(" ; ", shown);

        if (legs.Count > maxLegs)
            summary += $" ; ... +{legs.Count - maxLegs} more";

        return summary;
    }

    private record GroupedMarket(
        Market Market,
        OutcomeGroupInfo? Group
    );

    private record SnapshotItem(
        Market Market,
        OutcomeGroupInfo Group,
        BinaryOrderBookSnapshot Snapshot
    );

    private record OutcomeGroupInfo(
        string GroupKey,
        string OutcomeName,
        string OutcomeKind
    );

    private record BasketResult(
        bool Candidate,
        bool Executed,
        decimal? Cost,
        decimal? GuaranteedPayout)
    {
        public static BasketResult Empty =>
            new(false, false, null, null);

        public static BasketResult FromOpportunity(
            BasketArbOpportunity opportunity,
            bool candidate,
            bool executed)
        {
            return new BasketResult(
                Candidate: candidate,
                Executed: executed,
                Cost: opportunity.CostPerShare,
                GuaranteedPayout: opportunity.GuaranteedPayoutPerShare
            );
        }
    }

    private record GroupScanResult(
        string GroupKey,
        int MarketsInGroup,
        bool NoBasketCandidate,
        bool NoBasketExecuted,
        decimal? NoBasketCost,
        decimal? NoBasketGuaranteedPayout,
        bool YesBasketCandidate,
        bool YesBasketExecuted,
        decimal? YesBasketCost)
    {
        public static GroupScanResult Empty(string groupKey, int marketsInGroup) =>
            new(
                groupKey,
                marketsInGroup,
                false,
                false,
                null,
                null,
                false,
                false,
                null
            );
    }
}