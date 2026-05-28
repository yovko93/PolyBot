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
    private readonly Dictionary<string, DateTime> _recentlyExecuted = new();
    private readonly ConcurrentQueue<ExecutionAuditEvent> _audit = new();
    private readonly Dictionary<string, int> _duplicateSuppressionByGroup = new(StringComparer.OrdinalIgnoreCase);

    public VerifiedBasketExecutionCoordinator(IOptions<ExecutionOptions> executionOptions)
    {
        _execution = executionOptions.Value;
    }

    public IReadOnlyList<ExecutionAuditEvent> ListAudit(int limit = 200) => _audit.TakeLast(Math.Clamp(limit, 1, 1000)).ToArray();

    public void Audit(ExecutionAuditEvent e)
    {
        _audit.Enqueue(e);
        while (_audit.Count > 2000) _audit.TryDequeue(out _);
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

    public PaperPosition? OpenPaperPosition(VerifiedMultiOutcomeOpportunity opp, VerifiedBasketPreTradeValidationResult check, PaperPositionBook book)
    {
        AuditEvent(opp, "PaperOpenStarted", "Started", "PaperExecutionStart");
        var basket = new BasketArbOpportunity(opp.GroupKey, opp.Strategy, opp.Legs.Select(x => new BasketArbLeg(x.MarketId, x.NoTokenId, x.Question, x.Outcome, x.NoAsk, x.NoAskQuantity)).ToList(), check.Quantity, opp.NoAskSum, opp.GuaranteedPayout, opp.NetEdge, check.ExpectedProfit);
        var opened = book.AddBasketPosition(basket, check.Quantity, check.EstimatedCost, check.ExpectedProfit, "VerifiedMultiOutcome");
        if (opened is null)
        {
            AuditEvent(opp, "Rejected", "Rejected", "DuplicateOpenPosition");
            return null;
        }
        AuditEvent(opp, "PaperOpened", "Opened", "PaperOpened");
        return opened;
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

    private static string BuildIdempotencyKey(VerifiedMultiOutcomeOpportunity opp)
    {
        var rounded = string.Join("|", opp.Legs.OrderBy(x => x.MarketId).Select(x => Math.Round(x.NoAsk, 4)));
        var scanWindow = DateTime.UtcNow.ToString("yyyyMMddHH");
        var raw = $"{opp.Strategy}|{opp.GroupKey}|{opp.ActiveCostProfile}|{rounded}|{scanWindow}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }
}
