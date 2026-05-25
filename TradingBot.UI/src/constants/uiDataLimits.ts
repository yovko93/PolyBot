export const UIDataLimits = {
  MaxRecentLogs: 300,
  MaxOpportunities: 100,
  MaxTradeLogRows: 300,
  MaxScannerStatHistoryPoints: 500,
  MaxDiagnosticsNearMisses: 25,
  MaxRejectedCandidates: 50,
  MaxAuditEvents: 300,
  MaxChartPoints: 500,
} as const;

export function keepLatest<T>(items: T[], max: number): T[] {
  return items.length <= max ? items : items.slice(items.length - max);
}
