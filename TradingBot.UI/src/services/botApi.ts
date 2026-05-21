import type { BotStatus, Opportunity, PaperPosition, RiskState, ScannerStats, TerminalLogEntry, TradeLogEntry } from '../types/models';
import { BotSignalR } from './botSignalR';

const BASE_URL = ((import.meta as any).env.VITE_API_BASE_URL ?? 'http://localhost:5000').replace(/\/$/, '');
export const API = `${BASE_URL}/api/bot`;
export const HUB = `${BASE_URL}/hubs/bot`;

const mapStatus = (x: any): BotStatus => x;
const mapOpportunity = (x: any): Opportunity => x;
const mapPosition = (x: any): PaperPosition => x;
const mapTrade = (x: any): TradeLogEntry => x;
const mapRisk = (x: any): RiskState => x;
const mapScanner = (x: any): ScannerStats => x;
const mapLog = (x: any): TerminalLogEntry => x;
const mapEquity = (x: any) => x;

async function request<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(`${API}${path}`, { signal });
  if (!response.ok) throw new Error(`API ${path} failed: ${response.status}`);
  return response.json();
}

async function safeRequest<T>(path: string, fallback: T, signal?: AbortSignal): Promise<T> {
  try { return await request<T>(path, signal); } catch { return fallback; }
}

export const getBotHealth = async (signal?: AbortSignal): Promise<boolean> => {
  try {
    const health = await safeRequest<{ ok: boolean }>('/health', { ok: false }, signal);
    return !!health.ok;
  } catch { return false; }
};

export const getBotStatus = async (signal?: AbortSignal): Promise<BotStatus> => mapStatus(await request('/status', signal));
export const getOpportunities = async (signal?: AbortSignal): Promise<Opportunity[]> => (await safeRequest<any[]>('/opportunities', [], signal)).map(mapOpportunity);
export const getPositions = async (signal?: AbortSignal): Promise<PaperPosition[]> => (await safeRequest<any[]>('/positions', [], signal)).map(mapPosition);
export const getTradeLogs = async (signal?: AbortSignal): Promise<TradeLogEntry[]> => (await safeRequest<any[]>('/trade-log', [], signal)).map(mapTrade);
export const getScannerStats = async (signal?: AbortSignal): Promise<ScannerStats | null> => {
  const stats = await safeRequest<any | null>('/scanner-stats', null, signal);
  return stats ? mapScanner(stats) : null;
};
export const getRisk = async (signal?: AbortSignal): Promise<RiskState | null> => {
  const risk = await safeRequest<any | null>('/risk', null, signal);
  return risk ? mapRisk(risk) : null;
};
export const getTerminalLogs = async (signal?: AbortSignal): Promise<TerminalLogEntry[]> => (await safeRequest<any[]>('/logs/recent', [], signal)).map(mapLog);
export const getEquity = async (signal?: AbortSignal): Promise<Array<{ timestamp: string; equity: number }>> => (await safeRequest<any[]>('/equity', [], signal)).map(mapEquity);

type BotEventHandlers = {
  onStatus: (x: BotStatus) => void;
  onOpportunities: (x: Opportunity[]) => void;
  onOpportunityDetected: (x: Opportunity) => void;
  onTrades: (x: TradeLogEntry[]) => void;
  onPositions: (x: PaperPosition[]) => void;
  onRisk: (x: RiskState) => void;
  onScanner: (x: ScannerStats) => void;
  onLog: (x: TerminalLogEntry) => void;
  onEquity: (x: Array<{ timestamp: string; equity: number }>) => void;
  onHeartbeat: (x: string) => void;
  onConnectionState: (x: 'CONNECTED'|'RECONNECTING'|'DISCONNECTED') => void;
};

export const subscribeToBotEvents = async (handlers: BotEventHandlers): Promise<() => void> => {
  const hub = new BotSignalR(HUB);
  let polling: ReturnType<typeof setInterval> | null = null;
  let closed = false;

  const pollSnapshot = async () => {
    const [status, opportunities, positions, trades, scanner, risk, logs, equity] = await Promise.all([
      getBotStatus(), getOpportunities(), getPositions(), getTradeLogs(), getScannerStats(), getRisk(), getTerminalLogs(), getEquity()
    ]);
    handlers.onStatus(status); handlers.onOpportunities(opportunities); handlers.onPositions(positions); handlers.onTrades(trades);
    if (scanner) handlers.onScanner(scanner); if (risk) handlers.onRisk(risk);
    logs.slice(-50).forEach(handlers.onLog); handlers.onEquity(equity);
  };

  const ensurePolling = () => {
    if (polling || closed) return;
    polling = setInterval(() => { void pollSnapshot(); }, 3000);
  };
  const stopPolling = () => { if (polling) { clearInterval(polling); polling = null; } };

  hub.onState((s) => { handlers.onConnectionState(s); if (s === 'CONNECTED') { stopPolling(); } else { ensurePolling(); } });
  const unsubs = [
    hub.on('botStatusUpdated', (d) => handlers.onStatus(mapStatus(d))),
    hub.on('opportunitiesUpdated', (d) => handlers.onOpportunities(((d as any[]) ?? []).map(mapOpportunity))),
    hub.on('opportunityDetected', (d) => handlers.onOpportunityDetected(mapOpportunity(d))),
    hub.on('tradeLogUpdated', (d) => handlers.onTrades(((d as any[]) ?? []).map(mapTrade))),
    hub.on('positionsUpdated', (d) => handlers.onPositions(((d as any[]) ?? []).map(mapPosition))),
    hub.on('riskUpdated', (d) => handlers.onRisk(mapRisk(d))),
    hub.on('scannerStatsUpdated', (d) => handlers.onScanner(mapScanner(d))),
    hub.on('terminalLogAdded', (d) => handlers.onLog(mapLog(d))),
    hub.on('equityUpdated', (d) => handlers.onEquity(((d as any[]) ?? []).map(mapEquity))),
    hub.on('heartbeat', (d: any) => handlers.onHeartbeat(d?.timestamp ?? new Date().toISOString()))
  ];

  try { await hub.start(); stopPolling(); } catch { ensurePolling(); }

  return () => {
    closed = true;
    stopPolling();
    unsubs.forEach((u) => u());
    void hub.stop();
  };
};
