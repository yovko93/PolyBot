import { useEffect, useMemo, useRef, useState } from 'react';
import { API, HUB, getAllowlistRepairReport, getAutoCandidateVerification, getBotHealth, getBotStatus, getControls, getDryRunOrderPlans, getEquity, getExecutionAudit, getMultiOutcomeDiagnostics, getOpportunities, getPaperAccount, getPaperSettlements, getRuntimeHealth, getDiagnosticsDashboard, getPositions, getRisk, getScannerStats, getSingleMarketArbs, getSingleMarketPaperExecutions, getTerminalLogs, getTradeLogs, getVerifiedBasketScreener, pauseScanner, resumeScanner, subscribeToBotEvents } from '../services/botApi';
import { keepLatest, UIDataLimits } from '../constants/uiDataLimits';
import { addLogDeduped, dedupeLogSnapshot } from '../utils/logDedupe';


function parseCounterBag(value: string) {
  const bag: Record<string, number> = {};
  for (const part of value.replace(/[{}]/g, '').split(',')) {
    const [key, raw] = part.split(':');
    const numberValue = Number(raw);
    if (key && Number.isFinite(numberValue)) bag[key.trim()] = numberValue;
  }
  return bag;
}

function mergeStructuredStrategyCounters(pairs: Record<string, any>, source: Record<string, any>) {
  const fieldMap: Record<string, string> = {
    strategyScanCounts: 'scan',
    strategyCandidates: 'cand',
    strategyPositiveEdges: 'positive',
    strategyExecutionReady: 'ready',
    strategyPaperOpened: 'paper'
  };
  for (const [field, target] of Object.entries(fieldMap)) {
    const bag = source[field];
    if (!bag || typeof bag !== 'object') continue;
    for (const [strategy, value] of Object.entries(bag)) {
      const numeric = Number(value);
      if (!Number.isFinite(numeric)) continue;
      pairs.strategyCounters ??= {};
      pairs.strategyCounters[strategy] ??= {};
      pairs.strategyCounters[strategy][target] = numeric;
    }
  }
}

function parseStrategies(message: string, pairs: Record<string, any>) {
  const strategiesMatch = message.match(/Strategies=\{([^}]*)\}/);
  if (!strategiesMatch) return;
  pairs.strategyCounters ??= {};
  for (const item of strategiesMatch[1].split(',')) {
    const parts = item.split(':').map((x) => x.trim()).filter(Boolean);
    if (parts.length < 2) continue;
    const [name, mode, ...metrics] = parts;
    const counter = { ...(pairs.strategyCounters[name] ?? {}), mode };
    for (const metric of metrics) {
      const [key, raw] = metric.split('=');
      const numeric = Number(raw);
      counter[key] = Number.isFinite(numeric) ? numeric : raw;
    }
    pairs.strategyCounters[name] = counter;
  }
}

function parseSingleMarketSummaryLog(log: any) {
  const message = String(log?.message ?? '');
  if (!message.includes('[SINGLE_MARKET_FULL_CYCLE_SUMMARY]')) return null;
  const summary: Record<string, any> = { __updatedAt: log?.timestamp ?? new Date().toISOString() };
  for (const match of message.matchAll(/([A-Za-z][A-Za-z0-9_]*)=([^\s{}]+)/g)) {
    const [, rawKey, rawValue] = match;
    const key = rawKey.charAt(0).toLowerCase() + rawKey.slice(1);
    const numeric = Number(rawValue);
    summary[key] = Number.isFinite(numeric) ? numeric : rawValue;
  }
  return summary;
}

function parseRuntimeHealthLog(log: any) {
  const message = String(log?.message ?? '');
  const isRuntime = message.includes('[RUNTIME_HEALTH]') || message.includes('[SOAK_STATUS]');
  if (!isRuntime) return null;
  const pairs: Record<string, any> = { __source: message.includes('[SOAK_STATUS]') ? 'SOAK_STATUS' : 'RUNTIME_HEALTH', __updatedAt: log?.timestamp ?? new Date().toISOString() };
  for (const match of message.matchAll(/([A-Za-z][A-Za-z0-9_]*)=([^\s{}]+)/g)) {
    const [, rawKey, rawValue] = match;
    const key = rawKey.charAt(0).toLowerCase() + rawKey.slice(1);
    if (rawValue === 'true' || rawValue === 'false') pairs[key] = rawValue === 'true';
    else if (/^-?\d+(\.\d+)?$/.test(rawValue)) pairs[key] = Number(rawValue);
    else pairs[key] = rawValue;
  }
  if (pairs.discoveryMode && !pairs.discoverySelectedSource) pairs.discoverySelectedSource = pairs.discoveryMode;
  for (const match of message.matchAll(/(StrategyScanCounts|StrategyCandidates|StrategyPositiveEdges|StrategyExecutionReady|StrategyPaperOpened)=({[^}]*}|[^\s]+)/g)) {
    const key = match[1].charAt(0).toLowerCase() + match[1].slice(1);
    pairs[key] = parseCounterBag(match[2]);
  }
  parseStrategies(message, pairs);
  mergeStructuredStrategyCounters(pairs, pairs);
  return pairs;
}

function appendEquityPoint(points: any[], health: any) {
  const equity = Number(health?.paperEquity);
  if (!Number.isFinite(equity)) return points;
  const timestamp = new Date().toISOString();
  const last = points[points.length - 1];
  if (last && Number(last.equity) === equity && Date.now() - Date.parse(last.timestamp ?? timestamp) < 2500) return points;
  return keepLatest([...points, { timestamp, equity }], UIDataLimits.MaxChartPoints);
}

export function useBotData() {
  const [status, setStatus] = useState<any>(null); const [opps, setOpps] = useState<any[]>([]); const [positions, setPositions] = useState<any[]>([]); const [trades, setTrades] = useState<any[]>([]); const [risk, setRisk] = useState<any>(null); const [scanner, setScanner] = useState<any>(null); const [logs, setLogs] = useState<any[]>([]); const [equity, setEquity] = useState<any[]>([]); const [controls, setControls] = useState<any>(null);
  const [multiOutcomeDiagnostics, setMultiOutcomeDiagnostics] = useState<any>(null);
  const [verifiedBasketScreener, setVerifiedBasketScreener] = useState<any>(null);
  const [executionAudit, setExecutionAudit] = useState<any[]>([]);
  const [dryRunOrderPlans, setDryRunOrderPlans] = useState<any[]>([]);
  const [allowlistRepairReport, setAllowlistRepairReport] = useState<any>(null);
  const [singleMarketArbs, setSingleMarketArbs] = useState<any>(null);
  const [singleMarketPaperExecutions, setSingleMarketPaperExecutions] = useState<any[]>([]);
  const [paperAccount, setPaperAccount] = useState<any>(null);
  const [paperSettlements, setPaperSettlements] = useState<any[]>([]);
  const [autoCandidateVerification, setAutoCandidateVerification] = useState<any[]>([]);
  const [runtimeHealth, setRuntimeHealth] = useState<any>(null);
  const [diagnosticsDashboard, setDiagnosticsDashboard] = useState<any>(null);
  const [connectionStatus, setConnectionStatus] = useState('DISCONNECTED'); const [lastUpdated, setLastUpdated] = useState(''); const [lastHeartbeat, setLastHeartbeat] = useState(''); const [source, setSource] = useState('SNAPSHOT'); const [lastRestError, setLastRestError] = useState(''); const [lastEvent, setLastEvent] = useState('');
  const seenEvents = useRef(new Set<string>());

  useEffect(() => {
    const ac = new AbortController(); let cleanup = () => {}; let disposed = false;
    const go = async () => {
      try {
        const healthy = await getBotHealth(ac.signal);
        if (!healthy) setLastRestError('backend health endpoint unavailable');
        const [s, o, p, t, sc, r, l, eq, c, md, vbs, ea, drp, arr, acv, sma, smx, pa, ps, rh, dd] = await Promise.all([getBotStatus(ac.signal), getOpportunities(ac.signal), getPositions(ac.signal), getTradeLogs(ac.signal), getScannerStats(ac.signal), getRisk(ac.signal), getTerminalLogs(ac.signal), getEquity(ac.signal), getControls(ac.signal), getMultiOutcomeDiagnostics(ac.signal), getVerifiedBasketScreener(ac.signal), getExecutionAudit(ac.signal), getDryRunOrderPlans(ac.signal), getAllowlistRepairReport(ac.signal), getAutoCandidateVerification(ac.signal), getSingleMarketArbs(ac.signal), getSingleMarketPaperExecutions(ac.signal), getPaperAccount(ac.signal), getPaperSettlements(ac.signal), getRuntimeHealth(ac.signal), getDiagnosticsDashboard(ac.signal)]);
        setAutoCandidateVerification(acv); setStatus(s); setDiagnosticsDashboard(dd ? { ...dd, __source: 'DIAGNOSTICS_DASHBOARD', __updatedAt: new Date().toISOString() } : dd); setRuntimeHealth((dd ?? rh) ? { ...(rh ?? {}), ...(dd ?? {}), __source: dd ? 'DIAGNOSTICS_DASHBOARD' : 'REST', __updatedAt: new Date().toISOString() } : rh); setSingleMarketArbs(sma); setSingleMarketPaperExecutions(smx); setPaperAccount(pa); setPaperSettlements(ps); setMultiOutcomeDiagnostics(md); setVerifiedBasketScreener(vbs); setExecutionAudit(keepLatest(ea, UIDataLimits.MaxAuditRows)); setDryRunOrderPlans(keepLatest(drp, UIDataLimits.MaxDiagnosticsRows)); setAllowlistRepairReport(trimRepairReport(arr)); setOpps(keepLatest(o, UIDataLimits.MaxOpportunities)); setPositions(p); setTrades(keepLatest(t, UIDataLimits.MaxTradeLogRows)); setScanner(sc); setRisk(r); setLogs(dedupeLogSnapshot(keepLatest(l, UIDataLimits.MaxRecentLogs), UIDataLimits.MaxRecentLogs)); setEquity(keepLatest(eq, UIDataLimits.MaxChartPoints)); setControls(c); setConnectionStatus(healthy ? 'CONNECTED' : 'DEGRADED'); setSource(healthy ? 'POLLING FALLBACK' : 'SNAPSHOT'); setLastUpdated(new Date().toISOString());
      } catch (e: any) { setLastRestError(String(e)); setConnectionStatus('DISCONNECTED'); }

      const subscriptionCleanup = await subscribeToBotEvents({
        onStatus: (d) => { setLastEvent('botStatusUpdated'); setStatus(d); setLastUpdated(new Date().toISOString()); },
        onOpportunities: (d) => { setLastEvent('opportunitiesUpdated'); setOpps(keepLatest(d, UIDataLimits.MaxOpportunities)); setLastUpdated(new Date().toISOString()); },
        onOpportunityDetected: (d) => { setLastEvent('opportunityDetected'); setOpps((x: any[]) => keepLatest([d, ...x.filter(y => y.id !== d.id)], UIDataLimits.MaxOpportunities)); setLastUpdated(new Date().toISOString()); },
        onTrades: (d) => { setLastEvent('tradeLogUpdated'); setTrades(keepLatest(d, UIDataLimits.MaxTradeLogRows)); setLastUpdated(new Date().toISOString()); },
        onPositions: (d) => { setLastEvent('positionsUpdated'); setPositions(d); setLastUpdated(new Date().toISOString()); },
        onRisk: setRisk,
        onScanner: setScanner,
        onLog: (d) => {
          const parsedHealth = parseRuntimeHealthLog(d);
          const cycleSummary = parseSingleMarketSummaryLog(d);
          if (parsedHealth || cycleSummary) {
            setRuntimeHealth((current: any) => ({ ...(current ?? {}), ...(parsedHealth ?? {}), ...(cycleSummary ? { singleMarketFullCycleSummary: cycleSummary } : {}) }));
            setSource('LIVE BACKEND');
            setLastUpdated((parsedHealth ?? cycleSummary)?.__updatedAt ?? new Date().toISOString());
          }
          setLogs((x: any[]) => addLogDeduped(x, d, UIDataLimits.MaxRecentLogs));
        },
        onEquity: (d) => setEquity(keepLatest(d, UIDataLimits.MaxChartPoints)),
        onHeartbeat: setLastHeartbeat,
        onConnectionState: (s) => { if (!seenEvents.current.has(s)) seenEvents.current.add(s); setConnectionStatus(s); setSource(s === 'CONNECTED' ? 'LIVE BACKEND' : 'POLLING FALLBACK'); },
        onControls: setControls,
        onSingleMarketArbs: (d) => setSingleMarketArbs(d),
        onSingleMarketPaperExecutions: (d) => setSingleMarketPaperExecutions(d),
        onPaperAccount: (d) => setPaperAccount(d),
        onRuntimeHealth: (d) => { const stamped = d ? { ...d, __source: 'REST', __updatedAt: new Date().toISOString() } : d; setRuntimeHealth((current: any) => current?.__source === 'DIAGNOSTICS_DASHBOARD' ? current : stamped); if (d?.paperEquity != null) setEquity((x: any[]) => appendEquityPoint(x, d)); },
        onDiagnosticsDashboard: (d) => { const stamped = d ? { ...d, __source: 'DIAGNOSTICS_DASHBOARD', __updatedAt: new Date().toISOString() } : d; setDiagnosticsDashboard(stamped); if (stamped) setRuntimeHealth((current: any) => ({ ...(current ?? {}), ...stamped })); }
      });
      if (disposed) subscriptionCleanup();
      else cleanup = subscriptionCleanup;
    };

    void go();
    return () => { disposed = true; ac.abort(); cleanup(); };
  }, []);

  const trimRepairReport = (report: any) => report && Array.isArray(report.groups) ? { ...report, groups: keepLatest(report.groups, UIDataLimits.MaxRepairRows), repairSuggestions: keepLatest(report.repairSuggestions ?? [], UIDataLimits.MaxRepairRows) } : report;

  return useMemo(() => ({ API, HUB, status, runtimeHealth, diagnosticsDashboard, opps, positions, trades, risk, scanner, logs, equity, controls, multiOutcomeDiagnostics, verifiedBasketScreener, executionAudit, dryRunOrderPlans, allowlistRepairReport, autoCandidateVerification, singleMarketArbs, singleMarketPaperExecutions, paperAccount, paperSettlements, connectionStatus, lastHeartbeat, lastUpdated, source, lastRestError, lastEvent, pauseScanner, resumeScanner }), [status, runtimeHealth, diagnosticsDashboard, opps, positions, trades, risk, scanner, logs, equity, controls, multiOutcomeDiagnostics, verifiedBasketScreener, executionAudit, dryRunOrderPlans, allowlistRepairReport, autoCandidateVerification, singleMarketArbs, singleMarketPaperExecutions, paperAccount, paperSettlements, connectionStatus, lastHeartbeat, lastUpdated, source, lastRestError, lastEvent]);
}
