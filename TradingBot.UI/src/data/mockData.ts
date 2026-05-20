import type { BotStatus, Opportunity, PaperPosition, RiskState, ScannerStats, TradeLogEntry } from '../types/models';

export const botStatus: BotStatus = { mode: 'DRY RUN', connection: 'CONNECTED', cash: 1000.23, lockedCapital: 63.4, equity: 1012.12, realizedPnl: 12.12, lastScanTime: '2026-05-20T15:30:00Z' };
export const scannerStats: ScannerStats = { scansPerMin: 48, marketsTracked: 214, opportunitiesLastScan: 17, executableRate: 0.64, lastScanTime: botStatus.lastScanTime };
export const riskState: RiskState = { maxLockedCapital: 100, lockedCapital: 63.4, cash: 1000.23, equity: 1012.12, dailyLossLimit: -40, utilizationPct: 63.4 };
export const opportunities: Opportunity[] = [
  { id: '1', rank: 1, strategy: 'SingleMarketBuyBoth | BUY_YES_AND_BUY_NO', group: 'US Election', market: 'Will Candidate X win?', side: 'BOTH', edgePerShare: 0.008, expectedProfit: 0.8326, costOrProceeds: 99.14, guaranteedPayout: 100, qtyAvailable: 104, executable: true, status: 'READY' },
  { id: '2', rank: 2, strategy: 'MultiOutcomeGroup | BUY_ALL_NO_MUTUALLY_EXCLUSIVE', group: 'Fed Policy', market: 'Rate decision outcome basket', side: 'NO', edgePerShare: 0.006, expectedProfit: 0.4833, costOrProceeds: 71.2, guaranteedPayout: 75, qtyAvailable: 42, executable: true, status: 'READY' },
  { id: '3', rank: 3, strategy: 'CompleteSetSell | MINT_AND_SELL_YES_NO', group: 'Crypto ETF', market: 'Spot ETF approved by Q4?', side: 'BOTH', edgePerShare: 0.002, expectedProfit: 0.1277, costOrProceeds: 50.2, guaranteedPayout: 50, qtyAvailable: 30, executable: false, status: 'RISK_REJECTED' }
];
export const positions: PaperPosition[] = [
  { id: 'p1', market: 'Will Candidate X win?', side: 'YES/NO set', qty: 24, avgPrice: 0.492, mark: 0.499, unrealizedPnl: 0.168, status: 'OPEN' },
  { id: 'p2', market: 'Rate decision outcome basket', side: 'NO basket', qty: 16, avgPrice: 0.712, mark: 0.706, unrealizedPnl: -0.096, status: 'OPEN' }
];
export const tradeLogs: TradeLogEntry[] = [
  { id: 't1', time: '15:29:11', strategy: 'SingleMarketBuyBoth | BUY_YES_AND_BUY_NO', side: 'BUY BOTH', market: 'Will Candidate X win?', amount: 49.2, price: 0.492, edge: 0.008, status: 'FILLED' },
  { id: 't2', time: '15:29:42', strategy: 'MultiOutcomeGroup | BUY_ALL_NO_MUTUALLY_EXCLUSIVE', side: 'BUY NO', market: 'Rate decision outcome basket', amount: 28.48, price: 0.712, edge: 0.006, status: 'FILLED' },
  { id: 't3', time: '15:30:00', strategy: 'CompleteSetSell | MINT_AND_SELL_YES_NO', side: 'MINT/SELL', market: 'Spot ETF approved by Q4?', amount: 0, price: 0.502, edge: 0.002, status: 'REJECTED' }
];
export const equitySeries = Array.from({ length: 30 }, (_, i) => ({ t: i, equity: 1000 + Math.sin(i / 5) * 4 + i * 0.5 }));
export const terminalLogs = ['[15:29:57] SCAN complete | opportunities=17 | executable=11','[15:29:58] PAPER ARB SKIP | reason=max_locked_capital exceeded','[15:29:59] EXECUTE candidate #1 BUY_YES_AND_BUY_NO edge=0.008','[15:30:00] RISK gate PASS cash=1000.23 locked=63.4'];
