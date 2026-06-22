import type { BotControlState, BotStatus, MultiOutcomeDiagnostics, Opportunity, OpportunityDiagnostics, PaperPosition, RiskState, ScannerStats, TerminalLogEntry, TradeLogEntry } from '../types/models';
import { BotSignalR } from './botSignalR';
import { keepLatest, UIDataLimits } from '../constants/uiDataLimits';

const BASE_URL = ((import.meta as any).env.VITE_API_BASE_URL ?? 'http://localhost:5000').replace(/\/$/, '');
const POLLING_INTERVAL_MS = Number((import.meta as any).env.VITE_BOT_POLLING_INTERVAL_MS ?? 3000);
export const API = `${BASE_URL}/api/bot`;
export const HUB = `${BASE_URL}/hubs/bot`;

const mapStatus = (x: any): BotStatus => x;
const shouldDisplayOpportunity = (x: any): boolean => {
  const edge = Number(x?.edgePerShare ?? x?.edge ?? 0);
  const expectedProfit = Number(x?.expectedProfit ?? x?.profit ?? 0);
  const status = String(x?.status ?? '').toUpperCase();
  if (status === "DUPLICATE_SUPPRESSED") return true;
  if (edge <= 0 || expectedProfit <= 0) return false;
  if (status === 'SKIPPED' && edge <= 0) return false;
  return true;
};
const mapOpportunity = (x: any): Opportunity => x;
const mapPosition = (x: any): PaperPosition => x;
const mapTrade = (x: any): TradeLogEntry => x;
const mapRisk = (x: any): RiskState => x;
const mapScanner = (x: any): ScannerStats => x;
const mapLog = (x: any): TerminalLogEntry => x;
const mapEquity = (x: any) => x;
const mapControls = (x: any): BotControlState => x;

async function request<T>(path: string, signal?: AbortSignal, init?: RequestInit): Promise<T> {
  const response = await fetch(`${API}${path}`, { signal, ...init });
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
export const getOpportunities = async (signal?: AbortSignal): Promise<Opportunity[]> => (await safeRequest<any[]>('/opportunities', [], signal)).filter(shouldDisplayOpportunity).map(mapOpportunity);
export const getPositions = async (signal?: AbortSignal): Promise<PaperPosition[]> => (await safeRequest<any[]>('/paper/positions', [], signal)).map(mapPosition);
export const getTradeLogs = async (signal?: AbortSignal): Promise<TradeLogEntry[]> => keepLatest((await safeRequest<any[]>(`/paper/executions?limit=${UIDataLimits.MaxTradeLogRows}`, [], signal)).map(mapTrade), UIDataLimits.MaxTradeLogRows);
export const getScannerStats = async (signal?: AbortSignal): Promise<ScannerStats | null> => {
  const stats = await safeRequest<any | null>('/scanner-stats', null, signal);
  return stats ? mapScanner(stats) : null;
};
export const getRisk = async (signal?: AbortSignal): Promise<RiskState | null> => {
  const risk = await safeRequest<any | null>('/risk', null, signal);
  return risk ? mapRisk(risk) : null;
};
export const getTerminalLogs = async (signal?: AbortSignal): Promise<TerminalLogEntry[]> => keepLatest((await safeRequest<any[]>(`/logs/recent?limit=${UIDataLimits.MaxRecentLogs}`, [], signal)).map(mapLog), UIDataLimits.MaxRecentLogs);
export const getEquity = async (signal?: AbortSignal): Promise<Array<{ timestamp: string; equity: number }>> => keepLatest((await safeRequest<any[]>(`/equity?limit=${UIDataLimits.MaxChartPoints}`, [], signal)).map(mapEquity), UIDataLimits.MaxChartPoints);
export const getControls = async (signal?: AbortSignal): Promise<BotControlState> => mapControls(await request('/controls', signal));
export const getOpportunityDiagnostics = async (signal?: AbortSignal): Promise<OpportunityDiagnostics | null> => safeRequest<any | null>('/opportunity-diagnostics', null, signal);
export const getMultiOutcomeDiagnostics = async (signal?: AbortSignal): Promise<MultiOutcomeDiagnostics | null> => safeRequest<any | null>('/multi-outcome-diagnostics', null, signal);
export const getVerifiedBasketScreener = async (signal?: AbortSignal): Promise<any | null> => safeRequest<any | null>('/verified-basket-screener', null, signal);
export const getExecutionAudit = async (signal?: AbortSignal): Promise<any[]> => keepLatest(await safeRequest<any[]>('/execution-audit?limit=300', [], signal), UIDataLimits.MaxAuditRows);
export const getDryRunOrderPlans = async (signal?: AbortSignal): Promise<any[]> => keepLatest(await safeRequest<any[]>('/dry-run-order-plans?limit=100', [], signal), UIDataLimits.MaxDiagnosticsRows);
export const getSingleMarketArbs = async (signal?: AbortSignal): Promise<any | null> => safeRequest<any | null>('/single-market-arbs', null, signal);
export const getRuntimeHealth = async (signal?: AbortSignal): Promise<any | null> => safeRequest<any | null>('/runtime-health', null, signal);
export const getSingleMarketPaperExecutions = async (signal?: AbortSignal): Promise<any[]> => keepLatest(await safeRequest<any[]>('/single-market-paper-executions?limit=100', [], signal), 100);
export const getPaperAccount = async (signal?: AbortSignal): Promise<any | null> => safeRequest<any | null>('/paper/account', null, signal);
export const getPaperSettlements = async (signal?: AbortSignal): Promise<any[]> => safeRequest<any[]>('/paper/settlements', [], signal);
export const getAllowlistRepairReport = async (signal?: AbortSignal): Promise<any | null> => safeRequest<any | null>('/verified-allowlist-repair-report?limit=50', null, signal);
export const pauseScanner = async (): Promise<BotControlState> => mapControls(await request('/controls/pause', undefined, { method: 'POST' }));
export const resumeScanner = async (): Promise<BotControlState> => mapControls(await request('/controls/resume', undefined, { method: 'POST' }));

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
  onControls: (x: BotControlState) => void;
  onSingleMarketArbs: (x: any | null) => void;
  onSingleMarketPaperExecutions: (x: any[]) => void;
  onPaperAccount: (x: any | null) => void;
  onRuntimeHealth: (x: any | null) => void;
};


const memoryDiag = (globalThis as any).__botMemoryDiagnostics ?? ((globalThis as any).__botMemoryDiagnostics = {
  activeSignalRConnections: 0,
  activePollingIntervals: 0,
  registeredEventHandlers: 0,
});

export const subscribeToBotEvents = async (handlers: BotEventHandlers): Promise<() => void> => {
  let activeSignalRConnections = 0;
  let activePollingIntervals = 0;
  let registeredEventHandlers = 0;
  const hub = BotSignalR.getOrCreate(HUB);
  let polling: ReturnType<typeof setInterval> | null = null;
  let closed = false;
  let inFlight = false;
  let pollController: AbortController | null = null;

  const pollSnapshot = async () => {
    if (inFlight || closed) return;
    inFlight = true;
    pollController?.abort();
    pollController = new AbortController();
    try {
      const [status, opportunities, positions, trades, scanner, risk, logs, equity, controls, singleMarketArbs, singleMarketPaperExecutions, paperAccount, runtimeHealth] = await Promise.all([
        getBotStatus(pollController.signal), getOpportunities(pollController.signal), getPositions(pollController.signal), getTradeLogs(pollController.signal), getScannerStats(pollController.signal), getRisk(pollController.signal), getTerminalLogs(pollController.signal), getEquity(pollController.signal), getControls(pollController.signal), getSingleMarketArbs(pollController.signal), getSingleMarketPaperExecutions(pollController.signal), getPaperAccount(pollController.signal), getRuntimeHealth(pollController.signal)
      ]);
      handlers.onStatus(status); handlers.onOpportunities(opportunities); handlers.onPositions(positions); handlers.onTrades(trades);
      if (scanner) handlers.onScanner(scanner); if (risk) handlers.onRisk(risk);
      keepLatest(logs, Math.min(50, UIDataLimits.MaxRecentLogs)).forEach(handlers.onLog); handlers.onEquity(keepLatest(equity, UIDataLimits.MaxChartPoints));
      handlers.onControls(controls); handlers.onSingleMarketArbs(singleMarketArbs); handlers.onSingleMarketPaperExecutions(singleMarketPaperExecutions); handlers.onPaperAccount(paperAccount); handlers.onRuntimeHealth(runtimeHealth); handlers.onConnectionState('CONNECTED');
    } catch {
      handlers.onConnectionState('DISCONNECTED');
    } finally {
      inFlight = false;
    }
  };

  const ensurePolling = () => {
    if (polling || closed) return;
    polling = setInterval(() => { void pollSnapshot(); }, POLLING_INTERVAL_MS);
    activePollingIntervals += 1;
    memoryDiag.activePollingIntervals = activePollingIntervals;
  };
  const stopPolling = () => { if (polling) { clearInterval(polling); polling = null; activePollingIntervals = Math.max(0, activePollingIntervals - 1); memoryDiag.activePollingIntervals = activePollingIntervals; } };

  const unsubscribeState = hub.onState((s) => { handlers.onConnectionState(s); if (s === 'CONNECTED') { stopPolling(); } else { ensurePolling(); } });
  const unsubs = [
    unsubscribeState,
    hub.on('botStatusUpdated', (d) => handlers.onStatus(mapStatus(d))),
    hub.on('opportunitiesUpdated', (d) => handlers.onOpportunities(keepLatest(((d as any[]) ?? []).filter(shouldDisplayOpportunity).map(mapOpportunity), UIDataLimits.MaxOpportunities))),
    hub.on('opportunityDetected', (d) => { if (shouldDisplayOpportunity(d)) handlers.onOpportunityDetected(mapOpportunity(d)); }),
    hub.on('tradeLogUpdated', (d) => handlers.onTrades(keepLatest(((d as any[]) ?? []).map(mapTrade), UIDataLimits.MaxTradeLogRows))),
    hub.on('positionsUpdated', (d) => handlers.onPositions(((d as any[]) ?? []).map(mapPosition))),
    hub.on('riskUpdated', (d) => handlers.onRisk(mapRisk(d))),
    hub.on('scannerStatsUpdated', (d) => handlers.onScanner(mapScanner(d))),
    hub.on('terminalLogAdded', (d) => handlers.onLog(mapLog(d))),
    hub.on('equityUpdated', (d) => handlers.onEquity(keepLatest(((d as any[]) ?? []).map(mapEquity), UIDataLimits.MaxChartPoints))),
    hub.on('heartbeat', (d: any) => handlers.onHeartbeat(d?.timestamp ?? new Date().toISOString())),
    hub.on('controlsUpdated', (d) => handlers.onControls(mapControls(d))),
    hub.on('singleMarketArbsUpdated', (d) => handlers.onSingleMarketArbs(d ?? null)),
    hub.on('singleMarketPaperExecutionsUpdated', (d) => handlers.onSingleMarketPaperExecutions(keepLatest(((d as any[]) ?? []), 100)))
  ];
  registeredEventHandlers = unsubs.length;
  memoryDiag.registeredEventHandlers = registeredEventHandlers;

  try { await hub.start(); activeSignalRConnections = 1; memoryDiag.activeSignalRConnections = activeSignalRConnections; stopPolling(); } catch { ensurePolling(); }

  return () => {
    closed = true;
    stopPolling();
    pollController?.abort();
    unsubs.forEach((u) => u());
    registeredEventHandlers = 0;
    memoryDiag.registeredEventHandlers = registeredEventHandlers;
    activeSignalRConnections = 0;
    memoryDiag.activeSignalRConnections = activeSignalRConnections;
    void hub.stop();
  };
};
