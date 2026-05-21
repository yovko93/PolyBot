import { useEffect, useMemo, useRef, useState } from 'react';
import { API, HUB, createSignalR, getBotHealth, getBotStatus, getEquity, getOpportunities, getPositions, getRisk, getScannerStats, getTerminalLogs, getTradeLogs } from '../services/botApi';

export function useBotData() {
  const [status, setStatus] = useState<any>(null); const [opps, setOpps] = useState<any[]>([]); const [positions, setPositions] = useState<any[]>([]); const [trades, setTrades] = useState<any[]>([]); const [risk, setRisk] = useState<any>(null); const [scanner, setScanner] = useState<any>(null); const [logs, setLogs] = useState<any[]>([]); const [equity, setEquity] = useState<any[]>([]);
  const [connectionStatus, setConnectionStatus] = useState('DISCONNECTED'); const [lastUpdated, setLastUpdated] = useState(''); const [lastHeartbeat, setLastHeartbeat] = useState(''); const [source, setSource] = useState('SNAPSHOT'); const [lastRestError, setLastRestError] = useState(''); const [lastEvent, setLastEvent] = useState('');
  const seenEvents = useRef(new Set<string>());
  useEffect(() => { const ac = new AbortController(); let cleanup = () => {}; const go = async () => {
    try {
      const healthy = await getBotHealth(ac.signal); console.debug('[bot] health', healthy);
      if (!healthy) { setConnectionStatus('DISCONNECTED'); setLastRestError('health check failed'); return; }
      const [s, o, p, t, sc, r, l, eq] = await Promise.all([getBotStatus(ac.signal), getOpportunities(ac.signal), getPositions(ac.signal), getTradeLogs(ac.signal), getScannerStats(ac.signal), getRisk(ac.signal), getTerminalLogs(ac.signal), getEquity(ac.signal)]);
      setStatus(s); setOpps(o); setPositions(p); setTrades(t); setScanner(sc); setRisk(r); setLogs(l); setEquity(eq); setConnectionStatus('CONNECTED'); setSource('SNAPSHOT'); setLastUpdated(new Date().toISOString());
    } catch (e:any) { if (!lastRestError) console.error('[bot] REST failure', e); setLastRestError(String(e)); setConnectionStatus('DISCONNECTED'); return; }
    const hub = createSignalR();
    hub.onState((s) => { console.debug('[bot] signalr', s); setConnectionStatus(s); if (s === 'CONNECTED') setSource('LIVE BACKEND'); });
    const onEvt = (name: any, fn: (x:any)=>void) => hub.on(name, (d:any) => { if (!seenEvents.current.has(name)) { console.debug('[bot] event', name); seenEvents.current.add(name);} setLastEvent(name); fn(d); setLastUpdated(new Date().toISOString()); });
    const u = [onEvt('botStatusUpdated', setStatus), onEvt('opportunitiesUpdated', (d)=>setOpps(d)), onEvt('opportunityDetected', (d)=>setOpps((x:any[])=>[d,...x.filter(y=>y.id!==d.id)])), onEvt('tradeLogUpdated', setTrades), onEvt('positionsUpdated', setPositions), onEvt('riskUpdated', setRisk), onEvt('scannerStatsUpdated', setScanner), onEvt('terminalLogAdded', (d)=>setLogs((x:any[])=>[d,...x].slice(0,1000))), onEvt('heartbeat', (d)=>setLastHeartbeat(d.timestamp)), onEvt('equityUpdated', setEquity)];
    await hub.start(); cleanup = () => { u.forEach(f=>f()); void hub.stop(); };
  }; void go(); return () => { ac.abort(); cleanup(); }; }, []);
  return useMemo(() => ({ API, HUB, status, opps, positions, trades, risk, scanner, logs, equity, connectionStatus, lastHeartbeat, lastUpdated, source, lastRestError, lastEvent }), [status, opps, positions, trades, risk, scanner, logs, equity, connectionStatus, lastHeartbeat, lastUpdated, source, lastRestError, lastEvent]);
}
