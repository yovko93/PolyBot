export type StrategyType =
  | 'SingleMarketBuyBoth | BUY_YES_AND_BUY_NO'
  | 'MultiOutcomeGroup | BUY_ALL_NO_MUTUALLY_EXCLUSIVE'
  | 'CompleteSetSell | MINT_AND_SELL_YES_NO';

export interface Opportunity {
  id: string;
  rank: number;
  strategy: StrategyType;
  group: string;
  market: string;
  side: 'YES' | 'NO' | 'BOTH';
  edgePerShare: number;
  expectedProfit: number;
  costOrProceeds: number;
  guaranteedPayout: number;
  qtyAvailable: number;
  executable: boolean;
  status: 'READY' | 'RISK_REJECTED' | 'WATCH';
}

export interface TradeLogEntry {
  id: string;
  time: string;
  strategy: StrategyType;
  side: string;
  market: string;
  amount: number;
  price: number;
  edge: number;
  status: 'FILLED' | 'SKIPPED' | 'REJECTED';
}

export interface PaperPosition { id: string; market: string; side: string; qty: number; avgPrice: number; mark: number; unrealizedPnl: number; status: string; }
export interface RiskState { maxLockedCapital: number; lockedCapital: number; cash: number; equity: number; dailyLossLimit: number; utilizationPct: number; }
export interface ScannerStats { scansPerMin: number; marketsTracked: number; opportunitiesLastScan: number; executableRate: number; lastScanTime: string; }
export interface BotStatus { mode: 'DRY RUN' | 'PAPER' | 'LIVE'; connection: 'CONNECTED' | 'DEGRADED' | 'DISCONNECTED'; cash: number; lockedCapital: number; equity: number; realizedPnl: number; lastScanTime: string; }
