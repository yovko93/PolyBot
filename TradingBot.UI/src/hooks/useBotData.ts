import { useEffect, useMemo, useRef, useState } from 'react';
import { API, HUB, getAllowlistRepairReport, getBotHealth, getBotStatus, getControls, getDryRunOrderPlans, getEquity, getExecutionAudit, getMultiOutcomeDiagnostics, getOpportunities, getPaperAccount, getPaperSettlements, getRuntimeHealth, getPositions, getRisk, getScannerStats, getSingleMarketArbs, getSingleMarketPaperExecutions, getTerminalLogs, getTradeLogs, getVerifiedBasketScreener, pauseScanner, resumeScanner, subscribeToBotEvents } from '../services/botApi';
import { keepLatest, UIDataLimits } from '../constants/uiDataLimits';
import { addLogDeduped, dedupeLogSnapshot } from '../utils/logDedupe';


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
  const [runtimeHealth, setRuntimeHealth] = useState<any>(null);
  const [connectionStatus, setConnectionStatus] = useState('DISCONNECTED'); const [lastUpdated, setLastUpdated] = useState(''); const [lastHeartbeat, setLastHeartbeat] = useState(''); const [source, setSource] = useState('SNAPSHOT'); const [lastRestError, setLastRestError] = useState(''); const [lastEvent, setLastEvent] = useState('');
  const seenEvents = useRef(new Set<string>());

  useEffect(() => {
    const ac = new AbortController(); let cleanup = () => {}; let disposed = false;
    const go = async () => {
      try {
        const healthy = await getBotHealth(ac.signal);
        if (!healthy) setLastRestError('backend health endpoint unavailable');
        const [s, o, p, t, sc, r, l, eq, c, md, vbs, ea, drp, arr, sma, smx, pa, ps, rh] = await Promise.all([getBotStatus(ac.signal), getOpportunities(ac.signal), getPositions(ac.signal), getTradeLogs(ac.signal), getScannerStats(ac.signal), getRisk(ac.signal), getTerminalLogs(ac.signal), getEquity(ac.signal), getControls(ac.signal), getMultiOutcomeDiagnostics(ac.signal), getVerifiedBasketScreener(ac.signal), getExecutionAudit(ac.signal), getDryRunOrderPlans(ac.signal), getAllowlistRepairReport(ac.signal), getSingleMarketArbs(ac.signal), getSingleMarketPaperExecutions(ac.signal), getPaperAccount(ac.signal), getPaperSettlements(ac.signal), getRuntimeHealth(ac.signal)]);
        setStatus(s); setRuntimeHealth(rh); setSingleMarketArbs(sma); setSingleMarketPaperExecutions(smx); setPaperAccount(pa); setPaperSettlements(ps); setMultiOutcomeDiagnostics(md); setVerifiedBasketScreener(vbs); setExecutionAudit(keepLatest(ea, UIDataLimits.MaxAuditRows)); setDryRunOrderPlans(keepLatest(drp, UIDataLimits.MaxDiagnosticsRows)); setAllowlistRepairReport(trimRepairReport(arr)); setOpps(keepLatest(o, UIDataLimits.MaxOpportunities)); setPositions(p); setTrades(keepLatest(t, UIDataLimits.MaxTradeLogRows)); setScanner(sc); setRisk(r); setLogs(dedupeLogSnapshot(keepLatest(l, UIDataLimits.MaxRecentLogs), UIDataLimits.MaxRecentLogs)); setEquity(keepLatest(eq, UIDataLimits.MaxChartPoints)); setControls(c); setConnectionStatus(healthy ? 'CONNECTED' : 'DEGRADED'); setSource(healthy ? 'POLLING FALLBACK' : 'SNAPSHOT'); setLastUpdated(new Date().toISOString());
      } catch (e: any) { setLastRestError(String(e)); setConnectionStatus('DISCONNECTED'); }

      const subscriptionCleanup = await subscribeToBotEvents({
        onStatus: (d) => { setLastEvent('botStatusUpdated'); setStatus(d); setLastUpdated(new Date().toISOString()); },
        onOpportunities: (d) => { setLastEvent('opportunitiesUpdated'); setOpps(keepLatest(d, UIDataLimits.MaxOpportunities)); setLastUpdated(new Date().toISOString()); },
        onOpportunityDetected: (d) => { setLastEvent('opportunityDetected'); setOpps((x: any[]) => keepLatest([d, ...x.filter(y => y.id !== d.id)], UIDataLimits.MaxOpportunities)); setLastUpdated(new Date().toISOString()); },
        onTrades: (d) => { setLastEvent('tradeLogUpdated'); setTrades(keepLatest(d, UIDataLimits.MaxTradeLogRows)); setLastUpdated(new Date().toISOString()); },
        onPositions: (d) => { setLastEvent('positionsUpdated'); setPositions(d); setLastUpdated(new Date().toISOString()); },
        onRisk: setRisk,
        onScanner: setScanner,
        onLog: (d) => setLogs((x: any[]) => addLogDeduped(x, d, UIDataLimits.MaxRecentLogs)),
        onEquity: (d) => setEquity(keepLatest(d, UIDataLimits.MaxChartPoints)),
        onHeartbeat: setLastHeartbeat,
        onConnectionState: (s) => { if (!seenEvents.current.has(s)) seenEvents.current.add(s); setConnectionStatus(s); setSource(s === 'CONNECTED' ? 'LIVE BACKEND' : 'POLLING FALLBACK'); },
        onControls: setControls,
        onSingleMarketArbs: (d) => setSingleMarketArbs(d),
        onSingleMarketPaperExecutions: (d) => setSingleMarketPaperExecutions(d),
        onPaperAccount: (d) => setPaperAccount(d),
        onRuntimeHealth: (d) => { setRuntimeHealth(d); if (d?.paperEquity != null) setEquity((x: any[]) => appendEquityPoint(x, d)); }
      });
      if (disposed) subscriptionCleanup();
      else cleanup = subscriptionCleanup;
    };

    void go();
    return () => { disposed = true; ac.abort(); cleanup(); };
  }, []);

  const trimRepairReport = (report: any) => report && Array.isArray(report.groups) ? { ...report, groups: keepLatest(report.groups, UIDataLimits.MaxRepairRows), repairSuggestions: keepLatest(report.repairSuggestions ?? [], UIDataLimits.MaxRepairRows) } : report;

  return useMemo(() => ({ API, HUB, status, runtimeHealth, opps, positions, trades, risk, scanner, logs, equity, controls, multiOutcomeDiagnostics, verifiedBasketScreener, executionAudit, dryRunOrderPlans, allowlistRepairReport, singleMarketArbs, singleMarketPaperExecutions, paperAccount, paperSettlements, connectionStatus, lastHeartbeat, lastUpdated, source, lastRestError, lastEvent, pauseScanner, resumeScanner }), [status, runtimeHealth, opps, positions, trades, risk, scanner, logs, equity, controls, multiOutcomeDiagnostics, verifiedBasketScreener, executionAudit, dryRunOrderPlans, allowlistRepairReport, singleMarketArbs, singleMarketPaperExecutions, paperAccount, paperSettlements, connectionStatus, lastHeartbeat, lastUpdated, source, lastRestError, lastEvent]);
}
