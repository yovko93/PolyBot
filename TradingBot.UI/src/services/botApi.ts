import { botStatus, opportunities, positions, scannerStats, tradeLogs } from '../data/mockData';
import type { BotStatus, Opportunity, PaperPosition, ScannerStats, TradeLogEntry } from '../types/models';

export const getBotStatus = async (): Promise<BotStatus> => botStatus;
export const getOpportunities = async (): Promise<Opportunity[]> => opportunities;
export const getPositions = async (): Promise<PaperPosition[]> => positions;
export const getTradeLogs = async (): Promise<TradeLogEntry[]> => tradeLogs;
export const getScannerStats = async (): Promise<ScannerStats> => scannerStats;

export const subscribeToBotEvents = (onMessage: (event: string) => void): (() => void) => {
  const id = setInterval(() => onMessage(`[${new Date().toISOString()}] heartbeat:mock`), 5000);
  return () => clearInterval(id);
};
