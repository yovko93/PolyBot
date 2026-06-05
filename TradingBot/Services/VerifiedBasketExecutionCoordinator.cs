using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed class VerifiedBasketExecutionCoordinator
{
    private readonly object _gate = new();
    private readonly ExecutionOptions _execution;
    private readonly RuntimeStateOptions _runtime;
    private readonly Dictionary<string, DateTime> _recentlyExecuted = new();
    private readonly ConcurrentQueue<ExecutionAuditEvent> _audit = new();
    private readonly ConcurrentQueue<BasketOrderPlan> _dryRunPlans = new();
    private readonly ConcurrentQueue<FillSimulationResult> _fillSimulations = new();
    private readonly Dictionary<string, int> _duplicateSuppressionByGroup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuietAuditState> _quietAuditStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly TradingBotOptions _botOptions;

    public VerifiedBasketExecutionCoordinator(IOptions<ExecutionOptions> executionOptions)
        : this(executionOptions, Microsoft.Extensions.Options.Options.Create(new TradingBotOptions())) { }

    public VerifiedBasketExecutionCoordinator(IOptions<ExecutionOptions> executionOptions, IOptions<TradingBotOptions> botOptions)
    {
        _execution = executionOptions.Value;
        _botOptions = botOptions.Value;
        _runtime = _botOptions.RuntimeState;
    }

    public IReadOnlyList<ExecutionAuditEvent> ListAudit(int limit = 200) => _audit.TakeLast(Math.Clamp(limit, 1, _runtime.MaxExecutionAuditEvents)).ToArray();
    public IReadOnlyList<BasketOrderPlan> ListDryRunPlans(int limit = 50) => _dryRunPlans.TakeLast(Math.Clamp(limit, 1, _runtime.MaxDryRunOrderPlans)).ToArray();
    public IReadOnlyList<FillSimulationResult> ListFillSimulations(int limit = 50) => _fillSimulations.TakeLast(Math.Clamp(limit, 1, _runtime.MaxFillSimulations)).ToArray();

    public IReadOnlyList<object> ListDryRunPlanSummaries(int limit = 50)
    {
        var sims = ListFillSimulations(500).GroupBy(x => x.OrderPlanId).ToDictionary(g => g.Key, g => g.Last());
        return ListDryRunPlans(Math.Clamp(limit, 1, 500)).Select(p =>
        {
            sims.TryGetValue(p.Id, out var sim);
            return (object)new
            {
                plan = p,
                p.Id,
                p.OpportunityId,
                p.GroupKey,
                p.Title,
                p.Strategy,
                p.ActiveProfile,
                p.DryRunOnly,
                p.CreatedAt,
                p.ExpiresAt,
                status = p.Status.ToString(),
                p.LegsCount,
                p.PlannedQty,
                p.GuaranteedPayout,
                p.CostPerBasket,
                p.TotalEstimatedCost,
                p.ExpectedProfit,
                p.NetEdge,
                p.MaxNotional,
                p.Orders,
                p.ValidationWarnings,
                p.ValidationErrors,
                latestFillSimulationStatus = sim?.Status.ToString(),
                fullyFillableQty = sim?.FullyFillableQty,
                partialFillRisk = sim?.PartialFillRisk,
                worstLeg = sim?.WorstLeg,
                estimatedFilledCost = sim?.EstimatedFilledCost,
                plannedGrossEdgePerBasket = sim?.PlannedGrossEdgePerBasket,
                plannedNetEdgePerBasket = sim?.PlannedNetEdgePerBasket,
                fillAdjustedGrossEdgePerBasket = sim?.FillAdjustedGrossEdgePerBasket,
                fillAdjustedNetEdgePerBasket = sim?.FillAdjustedNetEdgePerBasket,
                plannedExpectedProfit = sim?.PlannedExpectedProfit,
                fillAdjustedExpectedProfit = sim?.FillAdjustedExpectedProfit
            };
        }).ToArray();
    }

    public void Audit(ExecutionAuditEvent e)
    {
        _audit.Enqueue(e);
        while (_audit.Count > _runtime.MaxExecutionAuditEvents) _audit.TryDequeue(out _);
    }

    public bool AuditQuiet(ExecutionAuditEvent e, int? maxPerHour = null, bool suppressRepeated = true, int everyNCycles = 100)
    {
        if (!suppressRepeated || !_botOptions.Diagnostics.OperationalQuietMode)
        {
            Audit(e);
            return true;
        }

        var key = $"{e.GroupKey}|{e.Stage}|{e.Status}|{e.Reason}";
        var material = $"{e.NetEdge:0.0000}|{e.ExpectedProfit:0.00}|{e.Cost:0.00}|{e.Qty:0.####}|{e.Details}";
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (!_quietAuditStates.TryGetValue(key, out var state) || now - state.WindowStartUtc >= TimeSpan.FromHours(1))
            {
                state = new QuietAuditState(now);
                _quietAuditStates[key] = state;
            }

            state.Cycles++;
            var changed = !string.Equals(state.LastMaterial, material, StringComparison.OrdinalIgnoreCase);
            var periodic = everyNCycles > 0 && state.Cycles % everyNCycles == 0;
            var first = state.Cycles == 1;
            var cap = Math.Max(0, maxPerHour ?? _botOptions.Logging.QuietModeMaxSameEventPerHour);
            if (!(first || changed || periodic) || (cap > 0 && state.EmittedThisHour >= cap))
                return false;

            state.LastMaterial = material;
            state.EmittedThisHour++;
        }

        Audit(e);
        return true;
    }

    public void RecordDryRunPlan(BasketOrderPlan plan)
    {
        _dryRunPlans.Enqueue(plan);
        while (_dryRunPlans.Count > _runtime.MaxDryRunOrderPlans) _dryRunPlans.TryDequeue(out _);
    }

    public void RecordFillSimulation(FillSimulationResult simulation)
    {
        _fillSimulations.Enqueue(simulation);
        while (_fillSimulations.Count > _runtime.MaxFillSimulations) _fillSimulations.TryDequeue(out _);
    }

    public int AuditCount => _audit.Count;
    public int DryRunPlanCount => _dryRunPlans.Count;
    public int FillSimulationCount => _fillSimulations.Count;

    public void ExportDryRunPlans(string path, string activeProfile, bool paperOnly, int limit = 50)
    {
        var payload = new
        {
            timestamp = DateTime.UtcNow,
            activeProfile,
            paperOnly,
            plans = ListDryRunPlans(limit).Select(plan => new
            {
                groupKey = plan.GroupKey,
                strategy = plan.Strategy,
                plannedQty = plan.PlannedQty,
                totalEstimatedCost = plan.TotalEstimatedCost,
                expectedProfit = plan.ExpectedProfit,
                netEdge = plan.NetEdge,
                dryRunOnly = plan.DryRunOnly,
                status = plan.Status.ToString(),
                latestFillSimulationStatus = ListFillSimulations(500).LastOrDefault(x => x.OrderPlanId == plan.Id)?.Status.ToString(),
                orders = plan.Orders.Select(o => new { o.MarketId, o.Question, o.TokenId, o.Side, o.PositionSide, o.Price, o.Quantity, o.EstimatedCost, o.DryRunOnly })
            })
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }


    public void ExportFillSimulations(string path, int limit = 50)
    {
        var payload = new
        {
            timestamp = DateTime.UtcNow,
            simulations = ListFillSimulations(limit).Select(s => new
            {
                s.OrderPlanId,
                s.GroupKey,
                status = s.Status.ToString(),
                plannedQty = s.RequestedQty,
                s.FullyFillableQty,
                s.SafeExecutableQty,
                s.EstimatedFilledCost,
                s.EstimatedFilledCostPerBasket,
                s.PlannedGrossEdgePerBasket,
                s.PlannedNetEdgePerBasket,
                s.FillAdjustedGrossEdgePerBasket,
                s.FillAdjustedNetEdgePerBasket,
                s.PlannedExpectedProfit,
                s.FillAdjustedExpectedProfit,
                s.SimulatedNoAskSum,
                s.EstimatedFees,
                s.EstimatedSlippage,
                s.SafetyBuffer,
                s.ProfileUsed,
                s.PartialFillRisk,
                s.AllOrNoneRecommended,
                s.LegResults
            })
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    public int MarkDuplicateSuppressed(string groupKey)
    {
        lock (_gate)
        {
            _duplicateSuppressionByGroup.TryGetValue(groupKey, out var c);
            c++;
            _duplicateSuppressionByGroup[groupKey] = c;
            return c;
        }
    }

    public int GetSuppressionCount(string groupKey)
    {
        lock (_gate)
        {
            return _duplicateSuppressionByGroup.TryGetValue(groupKey, out var c) ? c : 0;
        }
    }

    public void ExportAudit(string path, int limit = 500)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(ListAudit(limit), new JsonSerializerOptions { WriteIndented = true }));
    }

    public VerifiedBasketPreTradeValidationResult Validate(VerifiedMultiOutcomeOpportunity opp, PaperPositionBook book)
    {
        AuditEvent(opp, "PreTradeStarted", "Started", "ValidationStart");

        if (!string.Equals(opp.VerificationStatus, "Verified", StringComparison.OrdinalIgnoreCase)) return Reject(opp, "NotVerified");
        if (opp.ExecutableQty <= 0) return Reject(opp, "NonExecutableQuantity");
        if (opp.NetEdge <= 0) return Reject(opp, "NegativeAfterRecheck");
        if (opp.ExpectedProfit <= 0) return Reject(opp, "NonPositiveExpectedProfit");
        var costPerBasket = opp.Legs.Sum(x => x.NoAsk);
        var maxNotional = _execution.MaxNotionalPerBasket;
        var maxQtyByNotional = costPerBasket > 0 ? maxNotional / costPerBasket : 0m;
        var maxQtyByLiquidity = opp.Legs.Min(x => x.NoAskQuantity);
        var plannedQty = Math.Min(maxQtyByNotional, maxQtyByLiquidity);
        var totalCost = costPerBasket * plannedQty;
        var plannedExpectedProfit = opp.NetEdge * plannedQty;
        Console.WriteLine($"[SIZING_BASKET] Group={opp.GroupKey} CostPerBasket={costPerBasket} MaxNotional={maxNotional} MaxQtyByNotional={maxQtyByNotional} MaxQtyByLiquidity={maxQtyByLiquidity} PlannedQty={plannedQty} PlannedExpectedProfit={plannedExpectedProfit} TotalCost={totalCost}");
        var tolerance = 0.000001m;
        if (totalCost > maxNotional + tolerance) return Reject(opp, "MaxNotionalExceeded");
        if (opp.Legs.Any(x => x.NoAsk <= 0 || x.NoAskQuantity < x.PlannedQty)) return Reject(opp, "InsufficientLiquidity");
        if (book.GetOpenPositions().Any(x => x.GroupKey.Equals(opp.GroupKey, StringComparison.OrdinalIgnoreCase) && x.Strategy == opp.Strategy)) return Reject(opp, "DuplicatePosition");

        var idempotency = BuildIdempotencyKey(opp);
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var k in _recentlyExecuted.Where(x => x.Value < now).Select(x => x.Key).ToList()) _recentlyExecuted.Remove(k);
            var cooldown = TimeSpan.FromMinutes(Math.Max(1, _execution.DuplicateCooldownMinutes));
            if (_recentlyExecuted.TryGetValue(idempotency, out var until) && until > now) return Reject(opp, "RecentlyExecuted");
            _recentlyExecuted[idempotency] = now.Add(cooldown);
        }

        var approvedExpectedProfit = plannedExpectedProfit;
        var approved = new VerifiedBasketPreTradeValidationResult(true, "Approved", opp.NetEdge, plannedQty, totalCost, approvedExpectedProfit, idempotency);
        AuditEvent(opp, "PreTradeApproved", "Approved", "Approved");
        return approved;
    }

    public PaperPosition? OpenPaperPosition(VerifiedMultiOutcomeOpportunity opp, VerifiedBasketPreTradeValidationResult check, PaperPositionBook book, BasketOrderPlan? orderPlan, FillSimulationResult? fillSimulation)
    {
        if (orderPlan is null)
        {
            Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PaperOpenBlocked", "Blocked", "DryRunOrderPlanMissing", check.NetEdge, check.ExpectedProfit, check.EstimatedCost, check.Quantity, ""));
            Console.WriteLine($"[PAPER_OPEN_BLOCKED] Group={opp.GroupKey} Reason=DryRunOrderPlanMissing");
            return null;
        }

        if (!IsValidPaperOnlyOrderPlan(opp, check, orderPlan, out var planReason))
        {
            Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PaperOpenBlocked", "Blocked", planReason, check.NetEdge, check.ExpectedProfit, check.EstimatedCost, check.Quantity, $"OrderPlanId={orderPlan.Id}"));
            Console.WriteLine($"[PAPER_OPEN_BLOCKED] Group={opp.GroupKey} Reason={planReason}");
            return null;
        }

        AuditEvent(opp, "PaperOpenStarted", "Started", "PaperExecutionStart");
        if (fillSimulation is null)
        {
            AuditEvent(opp, "PaperOpenBlocked", "Blocked", "FillSimulationMissing");
            Console.WriteLine($"[PAPER_OPEN_BLOCKED] Group={opp.GroupKey} Reason=FillSimulationMissing");
            return null;
        }

        if (fillSimulation.Status != FillSimulationStatus.FullyFillable)
        {
            AuditEvent(opp, "PaperOpenBlocked", "Blocked", "FillSimulationFailed");
            Console.WriteLine($"[PAPER_OPEN_BLOCKED] Group={opp.GroupKey} Reason=FillSimulationFailed Status={fillSimulation.Status}");
            return null;
        }

        var legByMarket = fillSimulation.LegResults.ToDictionary(x => x.MarketId, StringComparer.OrdinalIgnoreCase);
        if (opp.Legs.Any(x => !legByMarket.ContainsKey(x.MarketId) || legByMarket[x.MarketId].SimulatedAveragePrice <= 0m))
        {
            AuditEvent(opp, "PaperOpenBlocked", "Blocked", "FillSimulationMissingPrices");
            Console.WriteLine($"[PAPER_OPEN_BLOCKED] Group={opp.GroupKey} Reason=FillSimulationMissingPrices");
            return null;
        }

        var qty = fillSimulation.SafeExecutableQty;
        var cost = fillSimulation.EstimatedFilledCost;
        var expectedProfit = fillSimulation.FillAdjustedExpectedProfit;
        var costPerBasket = fillSimulation.EstimatedFilledCostPerBasket;
        var netEdge = fillSimulation.FillAdjustedNetEdgePerBasket;
        var grossEdge = fillSimulation.FillAdjustedGrossEdgePerBasket;
        if (!ValidatePaperOpenInvariants(opp, qty, cost, expectedProfit, netEdge, grossEdge, fillSimulation, out var invariantReason))
        {
            Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PaperOpenBlocked", "Blocked", "PaperOpenInvariantFailed", netEdge, expectedProfit, cost, qty, invariantReason));
            Console.WriteLine($"[PAPER_OPEN_BLOCKED] Group={opp.GroupKey} Reason=PaperOpenInvariantFailed ExpectedProfitPlan={fillSimulation.PlannedExpectedProfit:0.####} ExpectedProfitPaper={expectedProfit:0.####} Details={invariantReason}");
            return null;
        }

        var basket = new BasketArbOpportunity(opp.GroupKey, opp.Strategy, opp.Legs.Select(x =>
        {
            var sim = legByMarket[x.MarketId];
            return new BasketArbLeg(x.MarketId, x.NoTokenId, x.Question, x.Outcome, sim.SimulatedAveragePrice, sim.AvailableQtyAtOrBelowLimit);
        }).ToList(), qty, costPerBasket, opp.GuaranteedPayout, netEdge, expectedProfit);
        var opened = book.AddBasketPosition(basket, qty, cost, expectedProfit, "VerifiedMultiOutcome", true, fillSimulation.Id, grossEdge, fillSimulation.ProfileUsed);
        if (opened is null)
        {
            AuditEvent(opp, "Rejected", "Rejected", "DuplicateOpenPosition");
            return null;
        }
        Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PaperOpened", "Opened", "PaperOpenedFromSimulatedFills", opened.NetEdgeAtOpen, opened.ExpectedProfit, opened.TotalCost, opened.Quantity, $"GrossEdgeAtOpen={opened.GrossEdgeAtOpen};FillAdjustedNetEdge={fillSimulation.FillAdjustedNetEdgePerBasket};ExpectedProfitAtOpen={opened.ExpectedProfit};Profile={fillSimulation.ProfileUsed}"));
        return opened;
    }


    private static bool IsValidPaperOnlyOrderPlan(VerifiedMultiOutcomeOpportunity opp, VerifiedBasketPreTradeValidationResult check, BasketOrderPlan plan, out string reason)
    {
        const decimal tolerance = 0.000001m;
        if (!string.Equals(plan.OpportunityId, opp.Id, StringComparison.OrdinalIgnoreCase)) { reason = "DryRunOrderPlanOpportunityMismatch"; return false; }
        if (!string.Equals(plan.GroupKey, opp.GroupKey, StringComparison.OrdinalIgnoreCase)) { reason = "DryRunOrderPlanGroupMismatch"; return false; }
        if (plan.Status is not (BasketOrderPlanStatus.PaperOnly or BasketOrderPlanStatus.Validated)) { reason = "DryRunOrderPlanNotValidated"; return false; }
        if (!plan.DryRunOnly || plan.Orders.Any(x => !x.DryRunOnly)) { reason = "DryRunOrderPlanNotDryRunOnly"; return false; }
        if (plan.ValidationErrors.Count > 0) { reason = plan.ValidationErrors[0]; return false; }
        if (Math.Abs(plan.PlannedQty - check.Quantity) > tolerance) { reason = "DryRunOrderPlanQuantityMismatch"; return false; }
        if (plan.Orders.Count != opp.LegsCount) { reason = "DryRunOrderPlanLegCountMismatch"; return false; }
        reason = string.Empty;
        return true;
    }

    private static bool ValidatePaperOpenInvariants(VerifiedMultiOutcomeOpportunity opp, decimal qty, decimal totalCost, decimal expectedProfitAtOpen, decimal netEdgeAtOpen, decimal grossEdgeAtOpen, FillSimulationResult fillSimulation, out string reason)
    {
        const decimal tolerance = 0.000001m;
        var checks = new List<string>();
        if (Math.Abs(expectedProfitAtOpen - fillSimulation.FillAdjustedExpectedProfit) > tolerance) checks.Add($"ExpectedProfitMismatch:{expectedProfitAtOpen}:{fillSimulation.FillAdjustedExpectedProfit}");
        if (Math.Abs(netEdgeAtOpen - fillSimulation.FillAdjustedNetEdgePerBasket) > tolerance) checks.Add($"NetEdgeMismatch:{netEdgeAtOpen}:{fillSimulation.FillAdjustedNetEdgePerBasket}");
        if (Math.Abs(totalCost - fillSimulation.EstimatedFilledCost) > tolerance) checks.Add($"CostMismatch:{totalCost}:{fillSimulation.EstimatedFilledCost}");
        if (Math.Abs(expectedProfitAtOpen - (qty * netEdgeAtOpen)) > tolerance) checks.Add($"ProfitFormulaMismatch:{expectedProfitAtOpen}:{qty * netEdgeAtOpen}");
        if (netEdgeAtOpen > grossEdgeAtOpen + tolerance) checks.Add($"NetGreaterThanGross:{netEdgeAtOpen}:{grossEdgeAtOpen}");
        reason = string.Join(";", checks);
        return checks.Count == 0;
    }

    public void ExportSnapshot(string path, string activeProfile, IEnumerable<VerifiedMultiOutcomeOpportunity> executable, IEnumerable<VerifiedBasketPreTradeValidationResult> results, IReadOnlyCollection<PaperPosition> paperPositions)
    {
        var payload = new
        {
            timestamp = DateTime.UtcNow,
            activeProfile,
            executableVerifiedBaskets = executable,
            preTradeResults = results,
            paperPositions = paperPositions.Select(x => new { x.PositionId, x.GroupKey, x.Strategy, x.TotalCost, x.ExpectedProfit, x.OpenedAtUtc, status = x.Status.ToString() }).ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private VerifiedBasketPreTradeValidationResult Reject(VerifiedMultiOutcomeOpportunity opp, string reason)
    {
        AuditEvent(opp, "Rejected", "Rejected", reason);
        return new(false, reason, opp.NetEdge, opp.ExecutableQty, opp.EstimatedCost, opp.ExpectedProfit);
    }

    private void AuditEvent(VerifiedMultiOutcomeOpportunity opp, string stage, string status, string reason)
        => Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, stage, status, reason, opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""));

    private sealed class QuietAuditState(DateTime windowStartUtc)
    {
        public DateTime WindowStartUtc { get; } = windowStartUtc;
        public int Cycles { get; set; }
        public int EmittedThisHour { get; set; }
        public string LastMaterial { get; set; } = string.Empty;
    }

    private static string BuildIdempotencyKey(VerifiedMultiOutcomeOpportunity opp)
    {
        var rounded = string.Join("|", opp.Legs.OrderBy(x => x.MarketId).Select(x => Math.Round(x.NoAsk, 4)));
        var scanWindow = DateTime.UtcNow.ToString("yyyyMMddHH");
        var raw = $"{opp.Strategy}|{opp.GroupKey}|{opp.ActiveCostProfile}|{rounded}|{scanWindow}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }
}
