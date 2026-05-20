import type { BotStatus, Opportunity, PaperPosition, RiskState, ScannerStats, TradeLogEntry } from '../types/models';

export const botStatus: BotStatus = { mode: 'DRY RUN', connection: 'CONNECTED', cash: 1000, lockedCapital: 0, equity: 1000, realizedPnl: 0, lastScanTime: '2026-05-20T15:30:00Z' };
export const scannerStats: ScannerStats = { scansPerMin: 52, marketsTracked: 214, opportunitiesLastScan: 15, executableRate: 0.6, lastScanTime: botStatus.lastScanTime };
export const riskState: RiskState = { maxLockedCapital: 100, lockedCapital: 0, cash: 1000, equity: 1000, dailyLossLimit: -40, utilizationPct: 0 };
export const opportunities: Opportunity[] = [
  { id: '1', rank: 1, strategy: 'SingleMarketBuyBoth | BUY_YES_AND_BUY_NO', group: 'US Election', market: 'Will Candidate X win?', side: 'BOTH', edgePerShare: 0.008, expectedProfit: 0.8326, costOrProceeds: 99.14, guaranteedPayout: 100, qtyAvailable: 104, executable: true, status: 'READY' },
  { id: '2', rank: 2, strategy: 'MultiOutcomeGroup | BUY_ALL_NO_MUTUALLY_EXCLUSIVE', group: 'Fed Policy', market: 'Rate decision outcome basket', side: 'NO', edgePerShare: 0.006, expectedProfit: 0.4833, costOrProceeds: 71.2, guaranteedPayout: 75, qtyAvailable: 42, executable: true, status: 'WATCH' },
  { id: '3', rank: 3, strategy: 'CompleteSetSell | MINT_AND_SELL_YES_NO', group: 'Crypto ETF', market: 'Spot ETF approved by Q4?', side: 'BOTH', edgePerShare: 0.002, expectedProfit: 0.1277, costOrProceeds: 50.2, guaranteedPayout: 50, qtyAvailable: 30, executable: false, status: 'RISK_REJECTED' }
];
export const positions: PaperPosition[] = [
  { id: 'p1', market: 'Will Candidate X win?', side: 'YES/NO set', qty: 24, avgPrice: 0.492, mark: 0.499, unrealizedPnl: 0.168, status: 'DRY RUN' },
  { id: 'p2', market: 'Rate decision outcome basket', side: 'NO basket', qty: 16, avgPrice: 0.712, mark: 0.706, unrealizedPnl: -0.096, status: 'PAPER SKIP' }
];
export const tradeLogs: TradeLogEntry[] = [
  { id: 't1', time: '15:29:11', strategy: 'SingleMarketBuyBoth | BUY_YES_AND_BUY_NO', side: 'BUY BOTH', market: 'Will Candidate X win?', amount: 49.2, price: 0.492, edge: 0.008, status: 'FILLED' },
  { id: 't2', time: '15:29:42', strategy: 'MultiOutcomeGroup | BUY_ALL_NO_MUTUALLY_EXCLUSIVE', side: 'BUY NO', market: 'Rate decision outcome basket', amount: 28.48, price: 0.712, edge: 0.006, status: 'SKIPPED' },
  { id: 't3', time: '15:30:00', strategy: 'CompleteSetSell | MINT_AND_SELL_YES_NO', side: 'MINT/SELL', market: 'Spot ETF approved by Q4?', amount: 0, price: 0.502, edge: 0.002, status: 'REJECTED' }
];
export const equitySeries = Array.from({ length: 30 }, (_, i) => ({ t: i, equity: 1000 + Math.sin(i / 5) * 4 + i * 0.4 }));
export const terminalLogs = [
  '[15:29:54] SCAN start | mode=DRY RUN | depth=5',
  '[15:29:55] candidate#14 MultiOutcomeGroup | BUY_ALL_NO_MUTUALLY_EXCLUSIVE edge/share=0.006 expected=0.4833',
  '[15:29:56] candidate#11 SingleMarketBuyBoth | BUY_YES_AND_BUY_NO edge/share=0.008 expected=0.8326',
  '[15:29:57] candidate#03 CompleteSetSell | MINT_AND_SELL_YES_NO edge/share=0.002 expected=0.1277',
  '[15:29:58] PAPER SKIP candidate#03 | reason=spread_widened',
  '[15:29:59] EXECUTABLE candidate#11 | yes=0.492 no=0.500 qty=104',
  '[15:30:00] DRY RUN place orders disabled | cash=1000 locked=0 equity=1000'
];
