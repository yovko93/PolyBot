using TradingBot.Options;

namespace TradingBot.Services;

public sealed record PaperEffectiveRiskSummary(
    int PaperPhase,
    string Source,
    decimal MaxPaperNotionalPerTrade,
    decimal MaxPaperTotalExposure,
    int MaxPaperOpenPerHour,
    int MaxPaperPositionsTotal,
    int MaxPaperPositionsPerStrategy,
    decimal SingleMarketMaxNotional,
    decimal VerifiedBasketMaxNotional,
    bool LegacyExecutionRiskIgnored,
    bool SyncedFromPaperRisk);

public static class PaperEffectiveRisk
{
    public static PaperEffectiveRiskSummary Apply(TradingBotOptions options, ExecutionOptions execution)
    {
        var paper = options.PaperRisk;
        var isPhase2 = options.TradingMode.PaperPhase >= 2;
        var source = isPhase2 ? "TradingBot:PaperRisk" : "TradingBot:LegacyExecutionRisk";
        var legacyIgnored = isPhase2 && options.PaperOnly && !options.EnableLiveExecution && !options.TradingMode.LiveTradingEnabled;

        if (isPhase2)
        {
            options.SingleMarketArb.MaxNotionalPerTrade = paper.MaxPaperNotionalPerTrade;
            options.SingleMarketArb.MaxTotalSingleMarketExposure = paper.MaxPaperTotalExposure;
            options.SingleMarketArb.MaxOpenSingleMarketPositions = paper.MaxPaperPositionsPerStrategy;
            options.VerifiedBasketArb.MaxNotionalPerTrade = paper.MaxPaperNotionalPerTrade;
            options.VerifiedBasketArb.MaxTotalVerifiedBasketExposure = paper.MaxPaperTotalExposure;
            options.VerifiedBasketArb.MaxOpenVerifiedBasketPositions = paper.MaxPaperPositionsPerStrategy;

            execution.MaxNotionalPerTrade = paper.MaxPaperNotionalPerTrade;
            execution.MaxNotionalPerBasket = paper.MaxPaperNotionalPerTrade;
            execution.MaxDailyNotional = Math.Max(execution.MaxDailyNotional, paper.MaxPaperTotalExposure);
            execution.MaxOpenPositions = paper.MaxPaperPositionsTotal;
            execution.MaxOpenBasketPositions = paper.MaxPaperPositionsPerStrategy;
            execution.MaxExposurePerGroup = paper.MaxPaperTotalExposure;
            execution.PaperOnly = true;
            execution.EnableLiveTrading = false;
            execution.EnableLiveOrderSubmission = false;
        }

        return new PaperEffectiveRiskSummary(
            options.TradingMode.PaperPhase,
            source,
            paper.MaxPaperNotionalPerTrade,
            paper.MaxPaperTotalExposure,
            paper.MaxPaperOpenPerHour,
            paper.MaxPaperPositionsTotal,
            paper.MaxPaperPositionsPerStrategy,
            options.SingleMarketArb.MaxNotionalPerTrade,
            options.VerifiedBasketArb.MaxNotionalPerTrade,
            legacyIgnored,
            isPhase2);
    }

    public static bool IsPaperPhase2RiskStillPhase1(TradingBotOptions options, ExecutionOptions execution)
    {
        if (options.TradingMode.PaperPhase < 2) return false;
        var target = options.PaperRisk.MaxPaperNotionalPerTrade;
        return execution.MaxNotionalPerTrade < target
            || execution.MaxNotionalPerBasket < target
            || options.SingleMarketArb.MaxNotionalPerTrade < target
            || options.VerifiedBasketArb.MaxNotionalPerTrade < target;
    }
}
