export const UIDataLimits = {
  MaxRecentLogs: 300,
  MaxScannerHistoryPoints: 300,
  MaxScannerStatHistoryPoints: 300,
  MaxAuditRows: 300,
  MaxAuditEvents: 300,
  MaxOpportunities: 100,
  MaxDiagnosticsRows: 100,
  MaxDiagnosticsNearMisses: 100,
  MaxRepairRows: 100,
  MaxRejectedCandidates: 100,
  MaxChartPoints: 300,
  MaxSignalREvents: 100,
  MaxTradeLogRows: 300,
} as const;

export function keepLatest<T>(items: T[], max: number): T[] {
  return items.length <= max ? items : items.slice(items.length - max);
}
