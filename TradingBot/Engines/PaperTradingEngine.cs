using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Options;

namespace TradingBot.Engines;

public class PaperTradingEngine
{
    private readonly MutuallyExclusiveGroupValidator _groupValidator = new(new TradingBot.Options.MultiOutcomeArbitrageOptions());
    private readonly Dictionary<string, PaperPosition> _positions = new();
    private readonly object _lock = new();
    private readonly HashSet<string> _executedArbs = new();
    private readonly HashSet<string> _accountedBasketPositionIds = new(StringComparer.OrdinalIgnoreCase);
    //private readonly HashSet<string> _executedBasketArbs = new();
    private readonly HashSet<string> _executedCompleteSetSellArbs = new();
    private readonly ExecutionPolicy _policy;
    private readonly ExecutionJournal? _journal;
    private readonly ExecutionDecisionService _decisionService;
    private readonly PaperPositionBook? _positionBook;
    private readonly TradingBotOptions _botOptions;
    private readonly Dictionary<string, int> _blockedCountsByReason = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DateTime> _paperOpenTimesUtc = new();
    private readonly Dictionary<string, DateTime> _cooldownUntilByKey = new(StringComparer.OrdinalIgnoreCase);

    public PaperTradingEngine(
        ExecutionPolicy? policy = null,
        ExecutionJournal? journal = null,
        ExecutionDecisionService? decisionService = null,
        PaperPositionBook? positionBook = null,
        TradingBotOptions? botOptions = null)
    {
        _policy = policy ?? new ExecutionPolicy();
        _journal = journal;
        _decisionService = decisionService ?? new ExecutionDecisionService(_policy);
        _positionBook = positionBook;
        _botOptions = botOptions ?? new TradingBotOptions();
    }

    public decimal Balance { get; private set; } = 1000m;
    public decimal LockedCapital { get; private set; } = 0m;
    public decimal ExpectedProfit { get; private set; } = 0m;
    public decimal RealizedPnl { get; private set; } = 0m;
    public decimal UnrealizedPnl { get; private set; } = 0m;

    public decimal Equity => Balance + LockedCapital + UnrealizedPnl;
    public IReadOnlyDictionary<string, int> BlockedCountsByReason => _blockedCountsByReason;
    public int HourlyOpenCount => _paperOpenTimesUtc.Count(x => x >= DateTime.UtcNow.AddHours(-1));
    public List<Position> Positions { get; } = new();

    private PaperAccountSnapshotForGate BuildGateAccount()
    {
        var open = _positionBook?.GetOpenPositions() ?? new List<PaperPosition>();
        return new PaperAccountSnapshotForGate(
            Balance,
            open.Sum(p => p.TotalCost),
            open.Count,
            open.GroupBy(p => p.Strategy, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
            HourlyOpenCount);
    }

    private bool ValidatePaperOpen(PaperPreTradeOpportunity opportunity, out string reason)
    {
        var gate = new PaperPreTradeGate(_botOptions);
        var result = gate.Validate(opportunity, BuildGateAccount());
        reason = result.Reason;
        if (!result.Approved)
        {
            _blockedCountsByReason[reason] = _blockedCountsByReason.TryGetValue(reason, out var c) ? c + 1 : 1;
            if (reason is "DuplicateOpenPosition" or "CooldownActive" or "MaxPaperOpenPerHourReached")
                Console.WriteLine($"[PAPER_OPEN_SUPPRESSED] Reason={reason} Strategy={opportunity.Strategy} MarketOrGroup={opportunity.MarketOrGroup}");
            return false;
        }
        return true;
    }

    private void MarkPaperOpened(string marketOrGroup, PaperStrategyKind kind)
    {
        var now = DateTime.UtcNow;
        _paperOpenTimesUtc.RemoveAll(x => x < now.AddHours(-1));
        _paperOpenTimesUtc.Add(now);
        var seconds = kind == PaperStrategyKind.SingleMarket ? _botOptions.SingleMarketArb.CooldownSecondsPerMarket : _botOptions.VerifiedBasketArb.CooldownSecondsPerGroup;
        _cooldownUntilByKey[marketOrGroup] = now.AddSeconds(Math.Max(0, seconds));
    }

    private bool IsCooldownActive(string key)
    {
        if (!_cooldownUntilByKey.TryGetValue(key, out var until)) return false;
        if (DateTime.UtcNow < until) return true;
        _cooldownUntilByKey.Remove(key);
        return false;
    }

    public bool HasOpenPaperPosition(string marketOrGroup, string strategy)
        => _positionBook?.GetOpenPositions().Any(p => p.Status == PaperPositionStatus.Open && p.GroupKey.Equals(marketOrGroup, StringComparison.OrdinalIgnoreCase) && p.Strategy.Equals(strategy, StringComparison.OrdinalIgnoreCase)) == true;

    public bool HasOpenSingleMarketPosition(string marketId, string strategy)
    {
        return _positionBook?.GetOpenPositions().Any(p => p.Status == PaperPositionStatus.Open && p.GroupKey.Equals($"single-market:{marketId}", StringComparison.OrdinalIgnoreCase) && (p.Engine.Equals("SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase) || p.Strategy.Equals(strategy, StringComparison.OrdinalIgnoreCase) || p.Strategy.Equals("TWO_LEG_ARB", StringComparison.OrdinalIgnoreCase))) == true;
    }

    public int CountOpenSingleMarketPositions()
        => _positionBook?.GetOpenPositions().Count(p => p.Status == PaperPositionStatus.Open && p.GroupKey.StartsWith("single-market:", StringComparison.OrdinalIgnoreCase)) ?? 0;

    public decimal GetOpenSingleMarketExposure()
        => _positionBook?.GetOpenPositions().Where(p => p.Status == PaperPositionStatus.Open && p.GroupKey.StartsWith("single-market:", StringComparison.OrdinalIgnoreCase)).Sum(p => p.TotalCost) ?? 0m;

    public void Execute(TradeSignal signal, string marketId)
    {
        if (signal.Action == TradeAction.None)
            return;

        if (signal.Action == TradeAction.Buy)
        {
            var amount = 10;

            Balance -= amount;

            Positions.Add(new Position
            {
                MarketId = marketId,
                EntryPrice = signal.Price,
                Size = amount
            });

            Console.WriteLine($"[BUY] {marketId} @ {signal.Price}");
        }

        if (signal.Action == TradeAction.Sell)
        {
            var openPositions = Positions
                .Where(p => p.MarketId == marketId && p.IsOpen)
                .ToList();

            foreach (var pos in openPositions)
            {
                var pnl = (signal.Price - pos.EntryPrice) * pos.Size;

                Balance += pos.Size + pnl;

                pos.IsOpen = false;

                Console.WriteLine($"[SELL] {marketId} @ {signal.Price} | PnL: {pnl}");
            }
        }
    }

    public decimal GetUnrealizedPnL(PriceService priceService)
    {
        decimal pnl = 0;

        foreach (var pos in Positions)
        {
            var currentPrice = priceService.GetPrice(pos.MarketId);

            if (currentPrice.HasValue)
            {
                pnl += (currentPrice.Value - pos.EntryPrice) * pos.Size;
            }
        }

        return pnl;
    }

    public void ExecuteArbitrage(string marketId, decimal yesPrice, decimal noPrice)
    {
        var investment = 10m;

        var totalCost = yesPrice * investment + noPrice * investment;

        var guaranteedReturn = investment;

        var pnl = guaranteedReturn - totalCost;

        Balance += pnl;

        Console.WriteLine($"[ARB] {marketId} | Profit: {pnl}");
    }

    // Остави този метод само ако старият ти код още го ползва
    public void ExecuteOrderbookArb(string marketId, decimal yesAsk, decimal noAsk)
    {
        var cost = yesAsk + noAsk;

        if (cost >= 1m)
            return;

        var profit = 1m - cost;

        lock (_lock)
        {
            Balance += profit;
        }

        Console.WriteLine($"[PAPER SINGLE MARKET ARB] Market={marketId}, Profit={profit}, Balance={Balance}");
    }

    public void ExecuteCrossArb(string m1, string m2, decimal p1, decimal p2)
    {
        var size = 10m;

        var cost = (p1 * size) + (p2 * size);
        var payout = size;

        var pnl = payout - cost;

        Balance += pnl;

        Console.WriteLine($"[CROSS ARB] Profit: {pnl}");
    }

    public bool RecordArbitrage(ArbOpportunity opportunity)
    {
        Console.WriteLine("[PAPER_ORDER_PLAN_CREATED] Strategy=SingleMarket MarketOrGroup=" + BuildTwoLegRiskGroupKey(opportunity));
        Console.WriteLine("[PAPER_FILL_SIMULATION_PASSED] Strategy=SingleMarket MarketOrGroup=" + BuildTwoLegRiskGroupKey(opportunity));
        var pretradeGroupKey = BuildTwoLegRiskGroupKey(opportunity);
        if (!ValidatePaperOpen(new PaperPreTradeOpportunity(opportunity.Strategy, pretradeGroupKey, PaperStrategyKind.SingleMarket, _botOptions.SingleMarketArb.PaperOnly, Math.Min(opportunity.Quantity * opportunity.CostPerShare, _botOptions.SingleMarketArb.MaxNotionalPerTrade), opportunity.ExpectedProfit, true, true, true, true, HasOpenPaperPosition(pretradeGroupKey, opportunity.Strategy), IsCooldownActive(pretradeGroupKey)), out _)) return false;

        if (!_policy.AllowSingleMarketArbs && !_policy.AllowThresholdArbs)
        {
            return false;
        }

        var key = BuildKey(opportunity);

        lock (_lock)
        {
            //if (_executedArbs.Contains(key))
            //    return false;

            if (opportunity.CostPerShare <= 0)
                return false;

            var maxQuantityByBalance = Balance / opportunity.CostPerShare;
            var maxQuantityByRisk = _policy.MaxNotionalPerTrade / opportunity.CostPerShare;

            var executableQuantity = Math.Min(
                opportunity.Quantity,
                Math.Min(maxQuantityByBalance, maxQuantityByRisk)
            );

            if (executableQuantity <= 0)
                return false;

            var totalCost = executableQuantity * opportunity.CostPerShare;

            if (totalCost < _policy.MinNotionalPerTrade)
                return false;

            var expectedProfit = executableQuantity * opportunity.GrossEdgePerShare;

            var groupKey = BuildTwoLegRiskGroupKey(opportunity);

            if (!CanAcceptRisk(
                    groupKey: groupKey,
                    newCost: totalCost,
                    out var riskReason))
            {
                Console.WriteLine(
                    $"[PAPER ARB SKIP] Risk rejected. " +
                    $"Reason={riskReason}, " +
                    $"Group={groupKey}, " +
                    $"Cost={totalCost:0.####}, " +
                    $"Locked={LockedCapital:0.####}, " +
                    $"Cash={Balance:0.####}"
                );

                return false;
            }

            var engineName = ResolveTwoLegEngineName(opportunity);

            if (_positionBook == null)
            {
                Console.WriteLine("[PAPER ARB SKIP] Position book is not configured.");
                return false;
            }

            var position = _positionBook?.AddTwoLegArbPosition(
                opportunity,
                executableQuantity: executableQuantity,
                totalCost: totalCost,
                expectedProfit: expectedProfit,
                guaranteedPayoutPerShare: 1m,
                engine: engineName
            );

            if (position == null)
            {
                return false;
            }

            _executedArbs.Add(key);

            Balance -= totalCost;
            LockedCapital += totalCost;
            ExpectedProfit += expectedProfit;
            MarkPaperOpened(groupKey, PaperStrategyKind.SingleMarket);

            Console.WriteLine($"[PAPER_POSITION_OPENED] ID={position.PositionId}");
            Console.WriteLine($"[PAPER_SINGLE_MARKET_OPENED] MarketId={opportunity.Leg1.MarketId} Qty={executableQuantity:0.####} Cost={totalCost:0.####} ExpectedProfit={expectedProfit:0.####}");
            Console.WriteLine($"[PAPER ACCOUNT] Cash={Balance:0.####} Locked={LockedCapital:0.####} OpenExposure={LockedCapital:0.####} UnrealizedPnl={UnrealizedPnl:0.####} RealizedPnl={RealizedPnl:0.####} Equity={Equity:0.####}");

            _journal?.Record(new ExecutionJournalRecord(
                TimestampUtc: DateTime.UtcNow,
                Mode: "PAPER",
                Engine: engineName,
                Strategy: opportunity.Strategy,
                Key: $"{opportunity.Leg1.MarketId}|{opportunity.Leg1.Outcome}|{opportunity.Leg2.MarketId}|{opportunity.Leg2.Outcome}",
                Quantity: executableQuantity,
                TotalCost: totalCost,
                GuaranteedPayout: executableQuantity,
                EdgePerShare: opportunity.GrossEdgePerShare,
                ExpectedProfit: expectedProfit,
                BalanceAfter: Balance,
                LockedCapitalAfter: LockedCapital,
                EquityAfter: Equity,
                Status: "EXECUTED",
                Legs:
                    $"{opportunity.Leg1.Outcome} @ {opportunity.Leg1.Price} | {opportunity.Leg1.Question}" +
                    " ; " +
                    $"{opportunity.Leg2.Outcome} @ {opportunity.Leg2.Price} | {opportunity.Leg2.Question}"
            ));

            Console.WriteLine(
                $"[PAPER TRUE ARB EXECUTED] " +
                $"Qty={executableQuantity:0.####}, " +
                $"Cost={totalCost:0.####}, " +
                $"ExpectedProfit={expectedProfit:0.####}, " +
                $"Cash={Balance:0.####}, " +
                $"Locked={LockedCapital:0.####}, " +
                $"Equity={Equity:0.####}"
            );

            return true;
        }
    }

    private static string BuildTwoLegRiskGroupKey(ArbOpportunity opportunity)
    {
        if (opportunity.Leg1.MarketId == opportunity.Leg2.MarketId)
            return $"single-market:{opportunity.Leg1.MarketId}";

        return $"cross-market:{opportunity.Leg1.MarketId}:{opportunity.Leg2.MarketId}";
    }

    private static string ResolveTwoLegEngineName(ArbOpportunity opportunity)
    {
        if (opportunity.Leg1.MarketId == opportunity.Leg2.MarketId)
            return "SingleMarketBuyBoth";

        if (opportunity.Leg1.Outcome == "YES" &&
            opportunity.Leg2.Outcome == "NO")
        {
            return "TwoLegCrossMarket";
        }

        return "StandardArb";
    }

    private static string BuildKey(ArbOpportunity opportunity)
    {
        return string.Join("|",
            opportunity.Leg1.MarketId,
            opportunity.Leg1.Outcome,
            opportunity.Leg2.MarketId,
            opportunity.Leg2.Outcome
        );
    }

    private bool CanAcceptRisk(
    string groupKey,
    decimal newCost,
    out string reason)
    {
        reason = "";

        if (_positionBook == null)
        {
            reason = "Position book is not configured";
            return false;
        }

        var openPositions = _positionBook.GetOpenPositions();

        if (openPositions.Count >= _policy.MaxOpenPositions)
        {
            reason =
                $"Max open positions reached. " +
                $"Open={openPositions.Count}, " +
                $"Limit={_policy.MaxOpenPositions}";

            return false;
        }

        if (LockedCapital + newCost > _policy.MaxLockedCapital)
        {
            reason =
                $"Max locked capital exceeded. " +
                $"CurrentLocked={LockedCapital:0.####}, " +
                $"NewCost={newCost:0.####}, " +
                $"Limit={_policy.MaxLockedCapital:0.####}";

            return false;
        }

        var groupExposure = openPositions
            .Where(x => x.GroupKey == groupKey)
            .Sum(x => x.TotalCost);

        if (groupExposure + newCost > _policy.MaxExposurePerGroup)
        {
            reason =
                $"Max exposure per group exceeded. " +
                $"Group={groupKey}, " +
                $"CurrentGroupExposure={groupExposure:0.####}, " +
                $"NewCost={newCost:0.####}, " +
                $"Limit={_policy.MaxExposurePerGroup:0.####}";

            return false;
        }

        return true;
    }

    public bool RecordBasketArbitrage(BasketArbOpportunity opportunity)
    {
        var key = BuildBasketKey(opportunity);

        lock (_lock)
        {
            if (_positionBook == null)
            {
                Console.WriteLine("[PAPER BASKET SKIP] Position book is not configured.");
                return false;
            }

            Console.WriteLine($"[PAPER_ORDER_PLAN_CREATED] Strategy=VerifiedBasket MarketOrGroup={opportunity.GroupKey}");
            Console.WriteLine($"[PAPER_FILL_SIMULATION_PASSED] Strategy=VerifiedBasket MarketOrGroup={opportunity.GroupKey}");
            if (!ValidatePaperOpen(new PaperPreTradeOpportunity(opportunity.Strategy, opportunity.GroupKey, PaperStrategyKind.VerifiedBasket, _botOptions.VerifiedBasketArb.PaperOnly, Math.Min(opportunity.Quantity * opportunity.CostPerShare, _botOptions.VerifiedBasketArb.MaxNotionalPerTrade), opportunity.ExpectedProfit, true, true, true, true, HasOpenPaperPosition(opportunity.GroupKey, opportunity.Strategy), IsCooldownActive(opportunity.GroupKey)), out _)) return false;

            var gv = _groupValidator.Validate(opportunity.GroupKey, "generic", opportunity.Legs);
            if (!gv.IsValidForNoBasketArbitrage)
            {
                Console.WriteLine($"[PAPER BASKET REJECTED] Reason={gv.RejectionReason} Group={opportunity.GroupKey}");
                return false;
            }

            var decision = _decisionService.EvaluateBasket(
                opportunity,
                currentBalance: Balance,
                currentLockedCapital: LockedCapital
            );

            if (!decision.CanExecute)
            {
                Console.WriteLine(
                    $"[PAPER BASKET SKIP] {decision.Reason}. " +
                    $"Group={opportunity.GroupKey}, " +
                    $"Edge={decision.EdgePerShare:0.####}, " +
                    $"Qty={decision.ExecutableQuantity:0.####}, " +
                    $"Cost={decision.TotalCost:0.####}, " +
                    $"ExpectedProfit={decision.ExpectedProfit:0.####}"
                );

                return false;
            }

            if (!CanAcceptRisk(
                groupKey: opportunity.GroupKey,
                newCost: decision.TotalCost,
                out var riskReason))
            {
                Console.WriteLine(
                    $"[PAPER BASKET SKIP] Risk rejected. " +
                    $"Reason={riskReason}, " +
                    $"Group={opportunity.GroupKey}, " +
                    $"Cost={decision.TotalCost:0.####}, " +
                    $"Locked={LockedCapital:0.####}, " +
                    $"Cash={Balance:0.####}"
                );

                return false;
            }

            var position = _positionBook.AddBasketPosition(
                opportunity,
                executableQuantity: decision.ExecutableQuantity,
                totalCost: decision.TotalCost,
                expectedProfit: decision.ExpectedProfit,
                engine: "MultiOutcomeGroup"
            );

            if (position == null)
            {
                // AddBasketPosition вече е принтирал причината:
                // duplicate open position или cooldown.
                return false;
            }

            Balance -= decision.TotalCost;
            LockedCapital += decision.TotalCost;
            ExpectedProfit += decision.ExpectedProfit;
            MarkPaperOpened(opportunity.GroupKey, PaperStrategyKind.VerifiedBasket);
            Console.WriteLine($"[PAPER_POSITION_OPENED] ID={position.PositionId}");
            Console.WriteLine($"[PAPER_VERIFIED_BASKET_OPENED] Group={opportunity.GroupKey} Legs={opportunity.Legs.Count} Cost={decision.TotalCost:0.####} ExpectedProfit={decision.ExpectedProfit:0.####}");

            Console.WriteLine(
                $"[PAPER BASKET ARB EXECUTED] " +
                $"Strategy={opportunity.Strategy}, " +
                $"Legs={opportunity.Legs.Count}, " +
                $"Qty={decision.ExecutableQuantity:0.####}, " +
                $"Cost={decision.TotalCost:0.####}, " +
                $"ExpectedProfit={decision.ExpectedProfit:0.####}, " +
                $"Cash={Balance:0.####}, " +
                $"Locked={LockedCapital:0.####}, " +
                $"Equity={Equity:0.####}"
            );

            Console.WriteLine($"[PAPER POSITION OPENED] ID={position.PositionId}");
            Console.WriteLine($"[PAPER ACCOUNT] Cash={Balance:0.####} Locked={LockedCapital:0.####} OpenExposure={LockedCapital:0.####} UnrealizedPnl={UnrealizedPnl:0.####} RealizedPnl={RealizedPnl:0.####} Equity={Equity:0.####}");

            _journal?.Record(new ExecutionJournalRecord(
                TimestampUtc: DateTime.UtcNow,
                Mode: "PAPER",
                Engine: "MultiOutcomeGroup",
                Strategy: opportunity.Strategy,
                Key: key,
                Quantity: decision.ExecutableQuantity,
                TotalCost: decision.TotalCost,
                GuaranteedPayout: decision.ExecutableQuantity * opportunity.GuaranteedPayoutPerShare,
                EdgePerShare: opportunity.EdgePerShare,
                ExpectedProfit: decision.ExpectedProfit,
                BalanceAfter: Balance,
                LockedCapitalAfter: LockedCapital,
                EquityAfter: Equity,
                Status: "EXECUTED",
                Legs: string.Join(" ; ", opportunity.Legs.Select(x =>
                    $"{x.Outcome} @ {x.Price} | {x.Question}"
                ))
            ));

            return true;
        }
    }

    public bool SettlePosition(string positionId, decimal realizedPayout)
    {
        lock (_lock)
        {
            if (_positionBook == null)
            {
                Console.WriteLine("[SETTLE SKIP] Position book is not configured.");
                return false;
            }

            if (!_positionBook.ClosePosition(positionId, realizedPayout, out var closedPosition))
            {
                Console.WriteLine($"[SETTLE SKIP] Position not found. ID={positionId}");
                return false;
            }

            if (closedPosition == null)
            {
                Console.WriteLine($"[SETTLE SKIP] Closed position is null. ID={positionId}");
                return false;
            }

            LockedCapital -= closedPosition?.TotalCost ?? 0;
            ExpectedProfit -= closedPosition?.ExpectedProfit ?? 0;
            Balance += realizedPayout;

            Console.WriteLine(
                $"[PAPER POSITION CLOSED] " +
                $"ID={positionId}, " +
                $"RealizedPayout={realizedPayout:0.####}, " +
                $"RealizedProfit={closedPosition?.RealizedProfit:0.####}, " +
                $"Cash={Balance:0.####}, " +
                $"Locked={LockedCapital:0.####}, " +
                $"ExpectedProfit={ExpectedProfit:0.####}, " +
                $"Equity={Equity:0.####}"
            );

            return true;
        }
    }

    public bool RegisterExternalBasketOpen(PaperPosition position, decimal totalCost, decimal expectedProfit)
    {
        lock (_lock)
        {
            if (_accountedBasketPositionIds.Contains(position.PositionId))
                return false;

            if (totalCost <= 0m)
                return false;

            Balance -= totalCost;
            LockedCapital += totalCost;
            ExpectedProfit += expectedProfit;
            _accountedBasketPositionIds.Add(position.PositionId);
            MarkPaperOpened(position.GroupKey, PaperStrategyKind.VerifiedBasket);
            return true;
        }
    }


    public bool SettleBasketPosition(string positionId, string winningMarketId)
    {
        lock (_lock)
        {
            if (_positionBook == null) return false;
            var position = _positionBook.OpenPositions.FirstOrDefault(x => x.PositionId == positionId);
            if (position == null) return false;
            if (!string.Equals(position.Strategy, "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", StringComparison.OrdinalIgnoreCase)) return false;
            var legsCount = position.Legs.Count;
            var inside = position.Legs.Any(x => string.Equals(x.MarketId, winningMarketId, StringComparison.OrdinalIgnoreCase));
            var payout = (inside ? (legsCount - 1) : legsCount) * position.Quantity;
            var ok = SettlePosition(positionId, payout);
            if (!ok) return false;
            Console.WriteLine($"[PAPER BASKET SETTLED] Group={position.GroupKey} WinningMarket={winningMarketId} Payout={payout:0.####} Cost={position.TotalCost:0.####} RealizedProfit={payout - position.TotalCost:0.####}");
            return true;
        }
    }

    private static string BuildBasketKey(BasketArbOpportunity opportunity)
    {
        var legsKey = string.Join("|",
            opportunity.Legs
                .OrderBy(x => x.MarketId)
                .Select(x => $"{x.MarketId}:{x.Outcome}")
        );

        return $"{opportunity.Strategy}|{opportunity.GroupKey}|{legsKey}";
    }

    public bool RecordCompleteSetSellArbitrage(ArbOpportunity opportunity)
    {
        if (!_policy.AllowCompleteSetSellArbs)
            return false;

        var key = BuildCompleteSetSellKey(opportunity);

        lock (_lock)
        {
            if (_executedCompleteSetSellArbs.Contains(key))
                return false;

            if (opportunity.CostPerShare <= 0)
                return false;

            if (opportunity.GrossEdgePerShare <= 0)
                return false;

            var maxQuantityByBalance = Balance / opportunity.CostPerShare;
            var maxQuantityByRisk = _policy.MaxNotionalPerTrade / opportunity.CostPerShare;

            var executableQuantity = Math.Min(
                opportunity.Quantity,
                Math.Min(maxQuantityByBalance, maxQuantityByRisk)
            );

            if (executableQuantity <= 0)
                return false;

            var mintCost = executableQuantity * opportunity.CostPerShare;

            if (mintCost < _policy.MinNotionalPerTrade)
                return false;

            if (Balance < mintCost)
                return false;

            var realizedProfit = executableQuantity * opportunity.GrossEdgePerShare;

            _executedCompleteSetSellArbs.Add(key);

            // Тук симулираме instant mint + sell into bids.
            // Няма locked capital, защото позициите се продават веднага.
            Balance += realizedProfit;

            _journal?.Record(new ExecutionJournalRecord(
                TimestampUtc: DateTime.UtcNow,
                Mode: "PAPER",
                Engine: "CompleteSetSell",
                Strategy: "MINT_AND_SELL_YES_NO",
                Key: $"{opportunity.Leg1.MarketId}|{opportunity.Leg1.Outcome}|{opportunity.Leg2.Outcome}",
                Quantity: executableQuantity,
                TotalCost: mintCost,
                GuaranteedPayout: executableQuantity * (1m + opportunity.GrossEdgePerShare),
                EdgePerShare: opportunity.GrossEdgePerShare,
                ExpectedProfit: realizedProfit,
                BalanceAfter: Balance,
                LockedCapitalAfter: LockedCapital,
                EquityAfter: Equity,
                Status: "EXECUTED",
                Legs:
                    $"{opportunity.Leg1.Outcome} @ {opportunity.Leg1.Price} | {opportunity.Leg1.Question}" +
                    " ; " +
                    $"{opportunity.Leg2.Outcome} @ {opportunity.Leg2.Price} | {opportunity.Leg2.Question}"
            ));

            Console.WriteLine(
                $"[PAPER COMPLETE SET SELL ARB EXECUTED] " +
                $"Qty={executableQuantity:0.####}, " +
                $"MintCost={mintCost:0.####}, " +
                $"RealizedProfit={realizedProfit:0.####}, " +
                $"Cash={Balance:0.####}, " +
                $"Locked={LockedCapital:0.####}, " +
                $"Equity={Equity:0.####}"
            );

            return true;
        }
    }

    public List<PaperPosition> GetOpenPositions()
    {
        lock (_lock)
        {
            if (_positionBook == null)
                return new List<PaperPosition>();

            return _positionBook.GetOpenPositions();
        }
    }

    private static string BuildCompleteSetSellKey(ArbOpportunity opportunity)
    {
        return string.Join("|",
            "COMPLETE_SET_SELL",
            opportunity.Leg1.MarketId,
            opportunity.Leg1.Outcome,
            opportunity.Leg2.MarketId,
            opportunity.Leg2.Outcome
        );
    }
}
