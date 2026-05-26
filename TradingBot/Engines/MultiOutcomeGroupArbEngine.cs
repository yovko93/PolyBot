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
    private readonly MutuallyExclusiveGroupValidator _validator;
    private readonly bool _logRejectedSummary;
    private readonly bool _logRejectedCandidates;
    private readonly int _rejectedSampleSize;
    private readonly bool _logRejectedSamplesOnlyInDebug;

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
        decimal requoteMinExpectedProfit = 0.25m,
        bool logRejectedCandidates = false,
        bool logRejectedSummary = true,
        int rejectedSampleSize = 5,
        bool logRejectedSamplesOnlyInDebug = true,
        MutuallyExclusiveGroupValidator? validator = null)
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
        _validator = validator ?? new MutuallyExclusiveGroupValidator(new TradingBot.Options.MultiOutcomeArbitrageOptions());
        _logRejectedCandidates = logRejectedCandidates;
        _logRejectedSummary = logRejectedSummary;
        _rejectedSampleSize = Math.Clamp(rejectedSampleSize, 1, 25);
        _logRejectedSamplesOnlyInDebug = logRejectedSamplesOnlyInDebug;
    }

    public async Task<MultiOutcomeScanReport> ScanAsync(
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

        var report = BuildReport(results);
        if (report.GroupsVerified == 0 && report.ExecutableGroups > 0) report = report with { ExecutableGroups = 0 };

        PrintSummary(report, _logRejectedSummary, _logRejectedCandidates && !_logRejectedSamplesOnlyInDebug, _rejectedSampleSize);
        return report;
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
        var groupKind = groupMarkets.First().Group.OutcomeKind;

        var snapshots = await LoadSnapshotsAsync(groupMarkets, semaphore, ct);

        if (snapshots.Count < 2)
            return GroupScanResult.Empty(groupKey, groupKind, snapshots.Select(x => x.Market).ToList(), snapshots.Count, "Candidate", "MissingLeg");

        var validation = _validator.Validate(groupKey, groupKind, snapshots.Select(x=>new BasketArbLeg(x.Snapshot.MarketId,x.Snapshot.MarketId,x.Snapshot.Question,"NO",x.Snapshot.NoAsk?.Price ?? 0m,x.Snapshot.NoAsk?.Size ?? 0m)).ToList());

        if (!validation.IsValidForNoBasketArbitrage)
        {
            return GroupScanResult.Empty(groupKey, groupKind, snapshots.Select(x => x.Market).ToList(), snapshots.Count, validation.VerificationStatus, validation.RejectionReason);
        }

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
            YesBasketCost: yesResult.Cost,
            Evaluated: true,
            VerificationStatus: "Verified",
            SkipReason: noResult.Executed ? "Executable" : (noResult.Candidate ? "NegativeEdge" : "MissingNoAsk"),
            NoBasketEdge: noResult.Edge,
            Kind: groupKind,
            Markets: snapshots.Select(x => x.Market).ToList()
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

    private static MultiOutcomeScanReport BuildReport(GroupScanResult[] results)
    {
        var scannedGroups = results.Length;
        var evaluated = results.Count(x => x.Evaluated);
        var executable = results.Count(x => x.NoBasketExecuted);
        var verified = results.Count(x=>x.VerificationStatus=="Verified");
        var bestCandidate = results.Where(x=>x.VerificationStatus!="Verified").OrderByDescending(x => x.NoBasketEdge ?? decimal.MinValue).FirstOrDefault();
        var bestVerified = results.Where(x=>x.VerificationStatus=="Verified").OrderByDescending(x => x.NoBasketEdge ?? decimal.MinValue).FirstOrDefault();
        var rejectedByReason = results.Where(x=>x.VerificationStatus!="Verified").GroupBy(x=>x.SkipReason).ToDictionary(g=>g.Key,g=>g.Count());
        var configuredIncomplete = results.Count(x => x.VerificationStatus == "ConfiguredButIncomplete" || x.SkipReason == "VerifiedGroupIncomplete");
        var rejected = results.Where(x=>x.VerificationStatus!="Verified").Select(x=>new RejectedSample(x.GroupKey,x.SkipReason)).ToArray();
        var candidatesForReview = results
            .Where(x => x.VerificationStatus != "Verified")
            .Select(x => new CandidateGroupReview(
                x.GroupKey,
                x.GroupKey,
                x.Kind,
                x.MarketsInGroup,
                x.VerificationStatus,
                x.SkipReason,
                x.NoBasketCost,
                x.NoBasketGuaranteedPayout.HasValue && x.NoBasketCost.HasValue ? x.NoBasketGuaranteedPayout.Value - x.NoBasketCost.Value : null,
                x.NoBasketEdge,
                x.NoBasketGuaranteedPayout,
                x.VerificationStatus == "ConfiguredButIncomplete"
                    ? new[] { "Allowlist entry is configured but incomplete (no marketIds/conditionIds)." }
                    : new[] { "Not manually verified", "Execution disabled until allowlisted with explicit marketIds or conditionIds" },
                x.Markets))
            .ToArray();
        return new MultiOutcomeScanReport(scannedGroups, evaluated, verified, results.Count(x=>x.VerificationStatus=="HighConfidence"), results.Count(x=>x.VerificationStatus=="Candidate"), executable, configuredIncomplete, bestCandidate?.NoBasketEdge ?? 0m, bestVerified?.NoBasketEdge ?? 0m, results.Where(x=>x.NoBasketExecuted).OrderByDescending(x=>x.NoBasketEdge ?? decimal.MinValue).FirstOrDefault()?.NoBasketEdge ?? 0m, bestVerified?.GroupKey ?? "", rejectedByReason.OrderByDescending(g=>g.Value).FirstOrDefault().Key ?? "None", rejectedByReason, rejected.Take(25).ToArray(), candidatesForReview);
    }

    private static void PrintSummary(MultiOutcomeScanReport report, bool logSummary, bool logSamples, int sampleSize)
    {
        var rejected = report.GroupsDetected - report.GroupsVerified;
        var reasonSummary = string.Join(",", report.RejectedByReason.Select(x => $"{x.Key}:{x.Value}"));
        if (logSummary)
            Console.WriteLine($"[MULTI_SCAN] Candidates={report.GroupsDetected} Verified={report.GroupsVerified} ConfiguredIncomplete={report.ConfiguredIncompleteGroups} Rejected={rejected} Executable={report.ExecutableGroups} TopReject={report.TopSkipReason} RejectedByReason={{{reasonSummary}}}");
        if (logSamples)
        {
            foreach (var grp in report.TopRejectedSamples.GroupBy(x => x.Reason))
            {
                var examples = string.Join(", ", grp.Select(x => x.GroupKey).Take(sampleSize));
                Console.WriteLine($"[MULTI_REJECTED_SAMPLE] Reason={grp.Key} Count={grp.Count()} Examples=[{examples}]");
            }
        }
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
        decimal? GuaranteedPayout,
        decimal? Edge = null)
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
                GuaranteedPayout: opportunity.GuaranteedPayoutPerShare,
                Edge: opportunity.EdgePerShare
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
        decimal? YesBasketCost,
        bool Evaluated,
        string VerificationStatus,
        string SkipReason,
        decimal? NoBasketEdge = null,
        string Kind = "generic",
        IReadOnlyList<Market>? Markets = null)
    {
        public static GroupScanResult Empty(string groupKey, string kind, IReadOnlyList<Market> markets, int marketsInGroup, string verificationStatus, string skipReason) =>
            new(groupKey, marketsInGroup, false, false, null, null, false, false, null, false, verificationStatus, skipReason, null, kind, markets);
    }

    public record MultiOutcomeScanReport(int GroupsDetected, int GroupsEvaluated, int GroupsVerified, int GroupsHighConfidence, int GroupsCandidate, int ExecutableGroups, int ConfiguredIncompleteGroups, decimal BestCandidateEdge, decimal BestVerifiedEdge, decimal BestExecutableEdge, string BestGroupKey, string TopSkipReason, IReadOnlyDictionary<string,int> RejectedByReason, IReadOnlyList<RejectedSample> TopRejectedSamples, IReadOnlyList<CandidateGroupReview> CandidateGroupsForReview);
    public record RejectedSample(string GroupKey, string Reason);
    public record CandidateGroupReview(string GroupKey, string Title, string Kind, int DetectedMarketsCount, string VerificationStatus, string RejectionReason, decimal? EstimatedNoBasketCost, decimal? EstimatedGrossEdge, decimal? EstimatedNetEdge, decimal? GuaranteedPayoutIfVerified, IReadOnlyList<string> Warnings, IReadOnlyList<Market>? Markets);
}
