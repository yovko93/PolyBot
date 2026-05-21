namespace TradingBot.Api;

public record BotStatusDto(string Mode,bool ScannerActive,string ConnectionStatus,decimal Cash,decimal LockedCapital,decimal Equity,decimal RealizedPnl,decimal ExpectedProfit,int OpenPositions,int SignalCount,DateTime LastScanTime,DateTime LastHeartbeat);
public record OpportunityDto(string Id,DateTime Timestamp,int Rank,string Strategy,string Group,string Market,string Side,decimal EdgePerShare,decimal ExpectedProfit,decimal CostOrProceeds,decimal GuaranteedPayout,decimal QtyAvailable,bool Executable,string Status,string? Reason,long Sequence);
public record TradeLogEntryDto(string Id,DateTime Timestamp,string Strategy,string Side,string Market,decimal Amount,decimal Price,decimal Edge,decimal ExpectedProfit,string Status,string? Reason,long Sequence);
public record PaperPositionDto(string Id,DateTime OpenedAt,DateTime? ClosedAt,string Strategy,string Group,IReadOnlyList<string> Legs,decimal Cost,decimal GuaranteedPayout,decimal ExpectedProfit,decimal? RealizedPayout,decimal? RealizedProfit,string Status,long Sequence);
public record RiskStateDto(decimal MaxNotionalPerTrade,decimal MinNotionalPerTrade,decimal MinEdgePerShare,decimal MinExpectedProfit,decimal MaxLockedCapital,decimal LockedCapital,int MaxOpenPositions,int OpenPositions,decimal MaxExposurePerGroup,Dictionary<string,decimal> CurrentExposureByGroup,bool AllowBasketArbs,bool AllowSingleMarketArbs,bool AllowCompleteSetSellArbs,bool AllowThresholdArbs,DateTime Timestamp,long Sequence);
public record ScannerStatsDto(int MarketsScanned,int OrderbooksScanned,int OpportunitiesDetected,int ExecutableOpportunities,int SkippedByRisk,long ScanDurationMs,DateTime LastScanStartedAt,DateTime LastScanCompletedAt,string? LastError,long Sequence);
public record TerminalLogEntryDto(string Id,DateTime Timestamp,string Level,string Source,string Message,long Sequence);
public record EquityPointDto(DateTime Timestamp, decimal Equity, long Sequence);
