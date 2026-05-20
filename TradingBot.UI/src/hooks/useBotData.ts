import { useEffect, useMemo, useState } from 'react';
import { createSignalR, getBotHealth, getBotStatus, getOpportunities, getPositions, getRisk, getScannerStats, getTerminalLogs, getTradeLogs } from '../services/botApi';
import type { BotStatus, ConnectionStatus, Opportunity, PaperPosition, RiskState, ScannerStats, TerminalLogEntry, TradeLogEntry } from '../types/models';

export function useBotData() {
  const [status, setStatus] = useState<BotStatus | null>(null); const [opps, setOpps] = useState<Opportunity[]>([]); const [positions, setPositions] = useState<PaperPosition[]>([]); const [trades, setTrades] = useState<TradeLogEntry[]>([]); const [risk, setRisk] = useState<RiskState | null>(null); const [scanner, setScanner] = useState<ScannerStats | null>(null); const [logs, setLogs] = useState<TerminalLogEntry[]>([]);
  const [connectionStatus, setConnectionStatus] = useState<ConnectionStatus>('DISCONNECTED'); const [lastUpdated, setLastUpdated] = useState(''); const [lastHeartbeat, setLastHeartbeat] = useState(''); const [source, setSource] = useState<'LIVE BACKEND'|'MOCK'|'SNAPSHOT'|'RECONNECTING'>('SNAPSHOT');
  useEffect(() => { const ac = new AbortController(); const go = async () => {
    const healthy = await getBotHealth(ac.signal); console.debug('[bot] health', healthy);
    if (!healthy) { setConnectionStatus('DISCONNECTED'); return; }
    try { const [s, o, p, t, sc, r, l] = await Promise.all([getBotStatus(ac.signal), getOpportunities(ac.signal), getPositions(ac.signal), getTradeLogs(ac.signal), getScannerStats(ac.signal), getRisk(ac.signal), getTerminalLogs(ac.signal)]); console.debug('[bot] snapshot loaded', { o: o.length, p: p.length, t: t.length, l: l.length }); setStatus(s); setOpps(o); setPositions(p); setTrades(t); setScanner(sc); setRisk(r); setLogs(l); setConnectionStatus('CONNECTED'); setSource('SNAPSHOT'); setLastUpdated(new Date().toISOString()); }
    catch (e) { console.debug('[bot] snapshot failed', e); setConnectionStatus('DISCONNECTED'); }
    const hub = createSignalR();
    hub.onState((s) => { console.debug('[bot] signalr state', s); if (s === 'RECONNECTING') { setConnectionStatus('RECONNECTING'); setSource('RECONNECTING'); } if (s === 'CONNECTED') { setConnectionStatus('CONNECTED'); setSource('LIVE BACKEND'); } if (s === 'DISCONNECTED') setConnectionStatus('DISCONNECTED'); });
    hub.start().catch(() => setConnectionStatus('DISCONNECTED'));
    const onEvt = <T,>(name: string, fn: (x: T) => void) => hub.on(name as never, (d: T) => { console.debug('[bot] event', name); fn(d); setLastUpdated(new Date().toISOString()); });
    const u = [onEvt<BotStatus>('botStatusUpdated', setStatus), onEvt<Opportunity[]>('opportunitiesUpdated', (d) => setOpps([...new Map(d.map(x=>[x.id,x])).values()].sort((a,b)=>b.edgePerShare-a.edgePerShare))), onEvt<Opportunity>('opportunityDetected', (d) => setOpps(x => [d, ...x.filter(y => y.id !== d.id)].slice(0, 500))), onEvt<TradeLogEntry[]>('tradeLogUpdated', setTrades), onEvt<TradeLogEntry>('tradeExecuted', (d) => setTrades(x => [d, ...x.filter(y => y.id !== d.id)].slice(0, 500))), onEvt<PaperPosition[]>('positionsUpdated', setPositions), onEvt<RiskState>('riskUpdated', setRisk), onEvt<ScannerStats>('scannerStatsUpdated', setScanner), onEvt<TerminalLogEntry>('terminalLogAdded', (d) => setLogs(x => [d, ...x].slice(0, 1000))), onEvt<{ timestamp: string }>('heartbeat', (d) => setLastHeartbeat(d.timestamp ?? new Date().toISOString()))];
    return () => { u.forEach(f => f()); void hub.stop(); };
  }; void go(); return () => ac.abort(); }, []);
  return useMemo(() => ({ status, opps, positions, trades, risk, scanner, logs, connectionStatus, lastHeartbeat, lastUpdated, source }), [status, opps, positions, trades, risk, scanner, logs, connectionStatus, lastHeartbeat, lastUpdated, source]);
}
