import { useEffect, useMemo, useRef, useState } from 'react';
import { createColumnHelper, flexRender, getCoreRowModel, useReactTable } from '@tanstack/react-table';
import { Area, AreaChart, CartesianGrid, ReferenceDot, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { useBotData } from './hooks/useBotData';

const money = (n: number) => `$${(n ?? 0).toFixed(2)}`;
const time = (v: any) => (v ? new Date(v).toLocaleTimeString() : '-');
const text = (v: any) => String(v ?? '-');
const connected = (status: string) => status.toLowerCase() === 'connected';
const first = (...values: any[]) => values.find((v) => v !== undefined && v !== null && v !== '');
const runtime = (h: any, key: string) => h?.[key] ?? h?.[key.charAt(0).toUpperCase() + key.slice(1)];

const normalDiagnosticPatterns = [
  'soak_status', 'soak status', 'runtime_health', 'runtime health',
  'strategy_summary', 'strategy summary', 'strategy_scan_result', 'strategy scan result',
  'allowlist_health', 'allowlist health', 'allowlist_refresh', 'allowlist refresh',
  'discovery_health', 'discovery health', 'diagnostics snapshot',
  'scanner lifecycle', 'scan complete', 'scan started', 'normal diagnostics'
];
const warningAlertPatterns = ['rest fetch failed', 'signalr disconnected', 'polling fallback', 'no backend', 'disconnected'];
const fatalAlertPatterns = [
  'scanner fault', 'unhandled exception', 'fatal backend error', 'fatal exception', 'circuit breaker opened',
  'orderbook circuit breaker', 'memory critical', 'live trading accidentally enabled', 'signing attempts',
  'paper execution error', 'paper execution failure'
];

function shortMessage(message: string) {
  const withoutPayload = message.replace(/\{[\s\S]*$/, '').replace(/\[[^\]]+\]\s*/, '').trim();
  return (withoutPayload || message).slice(0, 180);
}

function classifyAlert(log: any) {
  const raw = `${log.level ?? ''} ${log.source ?? ''} ${log.message ?? ''}`;
  const lower = raw.toLowerCase();
  if (normalDiagnosticPatterns.some((p) => lower.includes(p))) return null;
  if (lower.includes('scan') && !lower.includes('scanner fault')) return null;
  const fatal = fatalAlertPatterns.some((p) => lower.includes(p));
  const warning = warningAlertPatterns.some((p) => lower.includes(p));
  const restOrSignalRError = (lower.includes('rest') || lower.includes('signalr')) && (lower.includes('error') || lower.includes('failed') || lower.includes('unavailable'));
  const error = log.level === 'error' && (restOrSignalRError || lower.includes('scanner fault') || lower.includes('fatal') || lower.includes('memory critical'));
  if (!fatal && !warning && !error && !restOrSignalRError) return null;
  return {
    id: log.id,
    timestamp: log.timestamp,
    severity: fatal ? 'FATAL' : (error || restOrSignalRError) && !warning ? 'ERROR' : 'WARNING',
    source: String(log.source ?? 'runtime').replace(/^console$/i, 'Runtime'),
    message: shortMessage(String(log.message ?? 'Runtime alert'))
  };
}

function Panel({ title, children, active, compact }: any) {
  return <section className={`terminal-panel ${active ? 'terminal-panel-active' : ''} ${compact ? 'terminal-panel-compact' : ''}`}><h3 className="panel-title">{title}</h3>{children}</section>;
}

export default function App() {
  const d = useBotData();
  const [showDiagnostics, setShowDiagnostics] = useState(false);
  const fatalRef = useRef<HTMLDivElement>(null);
  useEffect(() => { if (fatalRef.current) fatalRef.current.scrollTop = 0; }, [d.logs]);

  const backendConnected = connected(d.connectionStatus);
  const health = d.runtimeHealth ?? {};
  const connectionLabel = backendConnected ? (d.source === 'POLLING FALLBACK' ? 'Connected via Polling' : 'Connected via SignalR') : 'Disconnected';
  const logAlerts = useMemo(() => d.logs.map(classifyAlert).filter(Boolean).slice(0, 20), [d.logs]);
  const connectionAlert = !backendConnected ? { id: 'connection', timestamp: d.lastUpdated, severity: 'WARNING', source: 'Connection', message: `Backend connection is ${d.connectionStatus}` } : null;
  const systemAlerts = connectionAlert ? [connectionAlert, ...logAlerts] : logAlerts;
  const openPositions = useMemo(() => (d.positions ?? []).filter((p: any) => (p.status ?? '').toUpperCase() === 'OPEN'), [d.positions]);
  const paperTrades = useMemo(() => keepPaperTrades(d.trades, d.singleMarketPaperExecutions, d.executionAudit), [d.trades, d.singleMarketPaperExecutions, d.executionAudit]);

  const column = createColumnHelper<any>();
  const tradeTable = useReactTable({
    data: paperTrades.slice(0, 60),
    columns: [
      column.accessor('timestamp', { header: 'TIME', cell: (i) => time(i.getValue()) }),
      column.accessor('strategy', { header: 'STRATEGY', cell: (i) => text(i.getValue()) }),
      column.accessor('market', { header: 'MARKET', cell: (i) => text(i.getValue()) }),
      column.accessor('status', { header: 'STATUS', cell: (i) => text(i.getValue()) }),
      column.accessor((r) => r.pnl ?? r.realizedProfit ?? r.expectedProfit ?? '-', { id: 'pnl', header: 'P/L', cell: (i) => text(i.getValue()) })
    ],
    getCoreRowModel: getCoreRowModel()
  });
  const positionTable = useReactTable({
    data: openPositions,
    columns: [
      column.accessor((r) => r.market ?? r.group ?? r.groupKey ?? '-', { id: 'market', header: 'MARKET', cell: (i) => text(i.getValue()) }),
      column.accessor('strategy', { header: 'STRATEGY', cell: (i) => text(i.getValue()) }),
      column.accessor((r) => r.quantity ?? r.qty ?? r.plannedQty ?? '-', { id: 'qty', header: 'QTY', cell: (i) => text(i.getValue()) }),
      column.accessor((r) => r.entryPrice ?? r.cost ?? '-', { id: 'entry', header: 'ENTRY', cell: (i) => text(i.getValue()) }),
      column.accessor((r) => r.unrealizedProfit ?? r.realizedProfit ?? r.status ?? '-', { id: 'unrealized', header: 'UNREALIZED / STATUS', cell: (i) => text(i.getValue()) })
    ],
    getCoreRowModel: getCoreRowModel()
  });

  const scanner = d.scanner ?? {};
  const paper = d.paperAccount ?? {};
  const equity = Number(first(runtime(health, 'paperEquity'), d.status?.equity, paper.equity, 0));
  const cash = Number(first(runtime(health, 'paperCash'), d.status?.cash, paper.cash, 0));
  const pnl = Number(first(runtime(health, 'paperRealizedPnl'), d.status?.realizedPnl, paper.realizedPnl, 0));
  const locked = Number(first(runtime(health, 'paperLocked'), d.status?.lockedCapital, paper.locked, 0));
  const equityValues = d.equity.map((p: any) => Number(p.equity)).filter(Number.isFinite);
  const chartHasData = equityValues.length > 0;
  const minEquity = chartHasData ? Math.min(...equityValues) : 0;
  const maxEquity = chartHasData ? Math.max(...equityValues) : 0;
  const flatEquity = chartHasData && minEquity === maxEquity;
  const singleEquityPoint = equityValues.length === 1;
  const domainPadding = flatEquity ? Math.max(5, Math.abs(minEquity) * 0.01) : Math.max(1, (maxEquity - minEquity) * 0.12);
  const chartDomain: [number, number] = [minEquity - domainPadding, maxEquity + domainPadding];

  return <div className="terminal-root min-h-screen terminal-font">
    <header className="status-strip" aria-label="Trading bot status">
      <div className="brand-chip">POLYBOT</div>
      <Metric label="Backend" value={connectionLabel} tone={backendConnected ? 'green' : 'yellow'} />
      <Metric label="Mode" value={d.status?.mode ?? `PaperPhase ${runtime(health, 'paperPhase') ?? 'PaperOnly'}`} tone="cyan" />
      <Metric label="Trading" value={runtime(health, 'strategyExecutionGloballyBlocked') ? 'Strategy blocked' : 'Paper / Live off'} tone={runtime(health, 'strategyExecutionGloballyBlocked') ? 'yellow' : 'green'} />
      <Metric label="Discovery" value={backendConnected ? discoveryLabel(health, scanner, d.controls) : 'Waiting'} tone={backendConnected ? (d.controls?.isPaused || runtime(health, 'discoveryReducedUniverse') ? 'yellow' : 'cyan') : 'yellow'} />
      <Metric label="Readiness" value={backendConnected ? readinessLabel(health, d.controls) : 'No backend'} tone={backendConnected ? (runtime(health, 'tradingReadiness') ? 'green' : 'yellow') : 'yellow'} />
      <Metric label="P/L" value={money(pnl)} tone={pnl < 0 ? 'red' : 'green'} />
    </header>

    <main className="clean-dashboard">
      <Panel title="P/L Vector" active>
        <div className="pl-summary"><BigStat label="Equity" value={money(equity)} tone="green" /><BigStat label="Cash" value={money(cash)} tone="green" /><BigStat label="Realized P/L" value={money(pnl)} tone={pnl < 0 ? 'red' : 'green'} /><BigStat label="Heartbeat" value={time(d.lastHeartbeat)} tone="muted" /><BigStat label="Locked" value={money(locked)} tone="yellow" /><BigStat label="Open Positions" value={openPositions.length} tone="cyan" /></div>
        <div className={`pl-chart ${chartHasData ? 'has-data' : 'is-empty'}`}>{chartHasData ? <ResponsiveContainer><AreaChart data={d.equity} margin={{ top: 14, right: 18, left: 4, bottom: 6 }}><defs><linearGradient id="plGlow" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="#35ff9c" stopOpacity={0.18} /><stop offset="100%" stopColor="#35ff9c" stopOpacity={0.01} /></linearGradient></defs><CartesianGrid stroke="rgba(53,255,156,.08)" vertical={false} /><XAxis dataKey="timestamp" hide /><YAxis domain={chartDomain} tick={{ fill: '#6ee7b7', fontSize: 10 }} width={58} /><Tooltip contentStyle={{ background: '#050807', border: '1px solid rgba(53,255,156,.35)', color: '#d9fff1' }} />{flatEquity ? <><ReferenceLine y={minEquity} stroke="#35ff9c" strokeWidth={2} strokeDasharray={singleEquityPoint ? '4 4' : undefined} />{singleEquityPoint && <ReferenceDot x={(d.equity[0] as any).timestamp} y={minEquity} r={4} fill="#35ff9c" stroke="#050807" />}</> : <Area type="monotone" dataKey="equity" stroke="#35ff9c" strokeWidth={3} fill="url(#plGlow)" />}</AreaChart></ResponsiveContainer> : <div className="chart-empty"><span>Waiting for portfolio/equity updates</span></div>}</div>
      </Panel>

      <section className="operations-grid">
        <Panel title="Recent Trades" active compact>{paperTrades.length ? <Table t={tradeTable} /> : <Empty label="Waiting for paper executions" />}</Panel>
        <Panel title="Open Positions" active compact>{openPositions.length ? <Table t={positionTable} /> : <Empty label="No open paper positions" />}</Panel>
      </section>

      <Panel title={`System Alerts${systemAlerts.length ? ` (${systemAlerts.length})` : ''}`} active={systemAlerts.length > 0} compact>
        <div ref={fatalRef} className="fatal-console">
          {systemAlerts.length ? systemAlerts.map((a: any) => <div key={a.id} className={`alert-line ${a.severity.toLowerCase()}`}><span>{a.severity}</span><b>{a.source}</b><p>{a.message}</p><time>{time(a.timestamp)}</time></div>) : <Empty label="No system alerts" />}
        </div>
      </Panel>

      <section className="diagnostics-shell">
        <button className="diagnostics-toggle" onClick={() => setShowDiagnostics((v) => !v)}>{showDiagnostics ? 'Hide diagnostics' : 'Show diagnostics'}</button>
        {showDiagnostics && <div className="diagnostics-grid"><MiniBlock title="Scanner" rows={[['Active', scanner.activeMarketsAvailable ?? scanner.activeMarketsDiscovered ?? 0], ['Pool', scanner.effectiveMarketPoolSize ?? scanner.effectiveMarketLimit ?? 0], ['Candidates', scanner.candidatesEvaluated ?? 0], ['Liquidity pass', scanner.marketsPassingLiquidity ?? 0], ['Best edge', scanner.bestEdgeIsAvailable ? scanner.bestEdgeSeen : 'N/A']]} /><MiniBlock title="Paper Summary" rows={[['Exposure', money(runtime(health, 'paperTotalExposure') ?? paper.totalExposure ?? locked)], ['Settlements', d.paperSettlements?.length ?? paper.settlements ?? 0], ['Rejects', Object.entries(paper.blockedCountsByReason ?? {}).map(([k, v]: any) => `${k}=${v}`).join(' ') || '-']]} /><MiniBlock title="Runtime" rows={[['Source', d.source], ['Discovery', discoveryLabel(health, scanner, d.controls)], ['Readiness', readinessLabel(health, d.controls)], ['REST error', d.lastRestError || '-'], ['Last event', d.lastEvent || '-'], ['Updated', time(d.lastUpdated)], ['Logs', d.logs.length]]} /><div className="raw-log-block"><h4>Raw diagnostics</h4>{d.logs.slice(0, 30).map((l: any) => <pre key={l.id}>{time(l.timestamp)} [{l.source}] {l.message}</pre>)}</div></div>}
      </section>
    </main>
  </div>;
}


function discoveryLabel(health: any, scanner: any, controls: any) {
  if (runtime(health, 'discoveryReducedUniverse')) return 'ReducedUniverseDiagnosticsOnly';
  return first(runtime(health, 'discoverySelectedSource'), scanner?.poolLimitReason, controls?.isPaused ? 'Paused' : undefined, 'Scanning');
}
function readinessLabel(health: any, controls: any) {
  if (controls?.isPaused) return 'Paused';
  return first(runtime(health, 'soakReadinessReason'), runtime(health, 'soakReadiness'), runtime(health, 'tradingReadiness') === true ? 'Ready' : undefined, 'Ready');
}
function keepPaperTrades(trades: any[] = [], singleMarketExecutions: any[] = [], audit: any[] = []) {
  const paperStatuses = new Set(['PAPER_EXECUTED', 'PAPER_OPENED', 'PAPER_CLOSED', 'FILLED', 'OPENED', 'CLOSED']);
  const fromTrades = trades.filter((t) => paperStatuses.has(String(t.status ?? '').toUpperCase()));
  const fromAudit = audit.filter((a) => String(a.status ?? a.eventType ?? '').toLowerCase().includes('paper'));
  return [...singleMarketExecutions, ...fromTrades, ...fromAudit].filter((t) => !String(t.message ?? t.source ?? '').toLowerCase().includes('diagnostic'));
}

function Metric({ label, value, tone }: any) { return <div className="status-metric"><span>{label}</span><strong className={`tone-${tone ?? 'green'}`}>{value}</strong></div>; }
function BigStat({ label, value, tone }: any) { return <div className="big-stat"><span>{label}</span><strong className={`tone-${tone ?? 'green'}`}>{value}</strong></div>; }
function MiniBlock({ title, rows }: any) { return <div className="mini-block"><h4>{title}</h4>{rows.map(([k, v]: any) => <div key={k} className="mini-row"><span>{k}</span><strong>{v}</strong></div>)}</div>; }
function Empty({ label }: any) { return <div className="empty-state">{label}</div>; }
function Table({ t }: any) { return <div className="table-wrap"><table className="terminal-table"><thead>{t.getHeaderGroups().map((hg: any) => <tr key={hg.id}>{hg.headers.map((h: any) => <th key={h.id}>{flexRender(h.column.columnDef.header, h.getContext())}</th>)}</tr>)}</thead><tbody>{t.getRowModel().rows.map((r: any) => <tr key={r.id}>{r.getVisibleCells().map((c: any) => <td key={c.id}>{flexRender(c.column.columnDef.cell, c.getContext())}</td>)}</tr>)}</tbody></table></div>; }
