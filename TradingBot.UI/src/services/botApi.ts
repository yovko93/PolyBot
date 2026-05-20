import { botStatus, opportunities, positions, riskState, scannerStats, terminalLogs, tradeLogs } from '../data/mockData';
import type { BotStatus, Opportunity, PaperPosition, RiskState, ScannerStats, TerminalLogEntry, TradeLogEntry } from '../types/models';
import { BotSignalR } from './botSignalR';

const API = (import.meta as any).env.VITE_BOT_API_BASE_URL ?? 'http://localhost:5000/api/bot';
const HUB = (import.meta as any).env.VITE_BOT_HUB_URL ?? 'http://localhost:5000/hubs/bot';
const USE_MOCK = String((import.meta as any).env.VITE_USE_MOCK_DATA ?? 'false') === 'true';
const j = async <T>(path: string, signal?: AbortSignal): Promise<T> => { const r = await fetch(`${API}${path}`, { signal }); if (!r.ok) throw new Error(`${path} ${r.status}`); return r.json(); };
export const getBotHealth = async (signal?: AbortSignal): Promise<boolean> => USE_MOCK ? true : !!(await j<{ok:boolean}>('/health', signal)).ok;
export const getBotStatus = async (signal?: AbortSignal): Promise<BotStatus> => USE_MOCK ? botStatus as unknown as BotStatus : j('/status', signal);
export const getOpportunities = async (signal?: AbortSignal): Promise<Opportunity[]> => USE_MOCK ? opportunities as unknown as Opportunity[] : j('/opportunities', signal);
export const getPositions = async (signal?: AbortSignal): Promise<PaperPosition[]> => USE_MOCK ? positions as unknown as PaperPosition[] : j('/positions', signal);
export const getTradeLogs = async (signal?: AbortSignal): Promise<TradeLogEntry[]> => USE_MOCK ? tradeLogs as unknown as TradeLogEntry[] : j('/trade-log', signal);
export const getScannerStats = async (signal?: AbortSignal): Promise<ScannerStats> => USE_MOCK ? scannerStats as unknown as ScannerStats : j('/scanner-stats', signal);
export const getRisk = async (signal?: AbortSignal): Promise<RiskState> => USE_MOCK ? riskState as unknown as RiskState : j('/risk', signal);
export const getTerminalLogs = async (signal?: AbortSignal): Promise<TerminalLogEntry[]> => USE_MOCK ? terminalLogs.map((x, i) => ({ id: String(i), timestamp: new Date().toISOString(), level: 'info', source: 'mock', message: x })) : j('/logs/recent', signal);
export const createSignalR = () => new BotSignalR(HUB);
