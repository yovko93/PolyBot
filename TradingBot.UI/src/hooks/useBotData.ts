import { useEffect, useMemo, useRef, useState } from 'react';
import { API, HUB, getBotHealth, getBotStatus, getControls, getEquity, getMultiOutcomeDiagnostics, getOpportunities, getPositions, getRisk, getScannerStats, getTerminalLogs, getTradeLogs, getVerifiedBasketScreener, pauseScanner, resumeScanner, subscribeToBotEvents } from '../services/botApi';
import { keepLatest, UIDataLimits } from '../constants/uiDataLimits';

export function useBotData() {
  const [status, setStatus] = useState<any>(null); const [opps, setOpps] = useState<any[]>([]); const [positions, setPositions] = useState<any[]>([]); const [trades, setTrades] = useState<any[]>([]); const [risk, setRisk] = useState<any>(null); const [scanner, setScanner] = useState<any>(null); const [logs, setLogs] = useState<any[]>([]); const [equity, setEquity] = useState<any[]>([]); const [controls, setControls] = useState<any>(null);
  const [multiOutcomeDiagnostics, setMultiOutcomeDiagnostics] = useState<any>(null);
  const [verifiedBasketScreener, setVerifiedBasketScreener] = useState<any>(null);
  const [connectionStatus, setConnectionStatus] = useState('DISCONNECTED'); const [lastUpdated, setLastUpdated] = useState(''); const [lastHeartbeat, setLastHeartbeat] = useState(''); const [source, setSource] = useState('SNAPSHOT'); const [lastRestError, setLastRestError] = useState(''); const [lastEvent, setLastEvent] = useState('');
  const seenEvents = useRef(new Set<string>());

  useEffect(() => {
    const ac = new AbortController(); let cleanup = () => {};
    const go = async () => {
      try {
        const healthy = await getBotHealth(ac.signal);
        if (!healthy) setLastRestError('backend health endpoint unavailable');
        const [s, o, p, t, sc, r, l, eq, c, md, vbs] = await Promise.all([getBotStatus(ac.signal), getOpportunities(ac.signal), getPositions(ac.signal), getTradeLogs(ac.signal), getScannerStats(ac.signal), getRisk(ac.signal), getTerminalLogs(ac.signal), getEquity(ac.signal), getControls(ac.signal), getMultiOutcomeDiagnostics(ac.signal), getVerifiedBasketScreener(ac.signal)]);
        setStatus(s); setMultiOutcomeDiagnostics(md); setVerifiedBasketScreener(vbs); setOpps(keepLatest(o, UIDataLimits.MaxOpportunities)); setPositions(p); setTrades(keepLatest(t, UIDataLimits.MaxTradeLogRows)); setScanner(sc); setRisk(r); setLogs(keepLatest(l, UIDataLimits.MaxRecentLogs)); setEquity(keepLatest(eq, UIDataLimits.MaxChartPoints)); setControls(c); setConnectionStatus(healthy ? 'CONNECTED' : 'DEGRADED'); setSource('SNAPSHOT'); setLastUpdated(new Date().toISOString());
      } catch (e: any) { setLastRestError(String(e)); setConnectionStatus('DISCONNECTED'); }

      cleanup = await subscribeToBotEvents({
        onStatus: (d) => { setLastEvent('botStatusUpdated'); setStatus(d); setLastUpdated(new Date().toISOString()); },
        onOpportunities: (d) => { setLastEvent('opportunitiesUpdated'); setOpps(keepLatest(d, UIDataLimits.MaxOpportunities)); setLastUpdated(new Date().toISOString()); },
        onOpportunityDetected: (d) => { setLastEvent('opportunityDetected'); setOpps((x: any[]) => keepLatest([d, ...x.filter(y => y.id !== d.id)], UIDataLimits.MaxOpportunities)); setLastUpdated(new Date().toISOString()); },
        onTrades: (d) => { setLastEvent('tradeLogUpdated'); setTrades(keepLatest(d, UIDataLimits.MaxTradeLogRows)); setLastUpdated(new Date().toISOString()); },
        onPositions: (d) => { setLastEvent('positionsUpdated'); setPositions(d); setLastUpdated(new Date().toISOString()); },
        onRisk: setRisk,
        onScanner: setScanner,
        onLog: (d) => setLogs((x: any[]) => keepLatest([d, ...x], UIDataLimits.MaxRecentLogs)),
        onEquity: (d) => setEquity(keepLatest(d, UIDataLimits.MaxChartPoints)),
        onHeartbeat: setLastHeartbeat,
        onConnectionState: (s) => { if (!seenEvents.current.has(s)) seenEvents.current.add(s); setConnectionStatus(s); setSource(s === 'CONNECTED' ? 'LIVE BACKEND' : 'POLLING FALLBACK'); },
        onControls: setControls
      });
    };

    void go();
    return () => { ac.abort(); cleanup(); };
  }, []);

  return useMemo(() => ({ API, HUB, status, opps, positions, trades, risk, scanner, logs, equity, controls, multiOutcomeDiagnostics, verifiedBasketScreener, connectionStatus, lastHeartbeat, lastUpdated, source, lastRestError, lastEvent, pauseScanner, resumeScanner }), [status, opps, positions, trades, risk, scanner, logs, equity, controls, multiOutcomeDiagnostics, verifiedBasketScreener, connectionStatus, lastHeartbeat, lastUpdated, source, lastRestError, lastEvent]);
}
