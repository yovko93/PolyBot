import { useEffect, useMemo, useRef, useState } from 'react';
import { createColumnHelper, flexRender, getCoreRowModel, useReactTable } from '@tanstack/react-table';
import { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { useBotData } from './hooks/useBotData';

const money = (n: number) => `$${(n ?? 0).toFixed(2)}`;
const time = (v: any) => (v ? new Date(v).toLocaleTimeString() : '-');
const text = (v: any) => String(v ?? '-');

function Panel({ title, children, active, compact }: any) {
  return <section className={`terminal-panel ${active ? 'terminal-panel-active' : ''} ${compact ? 'terminal-panel-compact' : ''}`}><h3 className="panel-title">{title}</h3>{children}</section>;
}

export default function App() {
  const d = useBotData();
  const [showDiagnostics, setShowDiagnostics] = useState(false);
  const fatalRef = useRef<HTMLDivElement>(null);
  useEffect(() => { if (fatalRef.current) fatalRef.current.scrollTop = 0; }, [d.logs]);

  const fatalLogs = useMemo(() => d.logs.filter((l: any) => {
    const m = `${l.level ?? ''} ${l.source ?? ''} ${l.message ?? ''}`.toLowerCase();
    return l.level === 'error' || m.includes('fatal') || m.includes('critical') || m.includes('exception') || m.includes('failed');
  }).slice(0, 20), [d.logs]);
  const openPositions = useMemo(() => (d.positions ?? []).filter((p: any) => (p.status ?? '').toUpperCase() === 'OPEN'), [d.positions]);

  const column = createColumnHelper<any>();
  const tradeTable = useReactTable({ data: d.trades.slice(0, 80), columns: ['timestamp', 'strategy', 'market', 'status'].map((k) => column.accessor(k, { header: k.toUpperCase(), cell: (i) => text(i.getValue()) })), getCoreRowModel: getCoreRowModel() });
  const positionTable = useReactTable({ data: openPositions, columns: ['group', 'strategy', 'cost', 'expectedProfit', 'realizedProfit', 'lockedCapital', 'openedAt', 'status'].map((k) => column.accessor(k, { header: k.toUpperCase(), cell: (i) => text(i.getValue()) })), getCoreRowModel: getCoreRowModel() });

  const scanner = d.scanner ?? {};
  const paper = d.paperAccount ?? {};
  const equity = d.status?.equity ?? paper.equity ?? 0;
  const cash = d.status?.cash ?? paper.cash ?? 0;
  const pnl = d.status?.realizedPnl ?? paper.realizedPnl ?? 0;

  return <div className="terminal-root min-h-screen terminal-font">
    <header className="status-strip" aria-label="Trading bot status">
      <div className="brand-chip">POLYBOT</div>
      <Metric label="Backend" value={d.connectionStatus} tone={d.connectionStatus === 'connected' ? 'green' : 'yellow'} />
      <Metric label="Mode" value={d.status?.mode ?? 'PaperOnly'} tone="cyan" />
      <Metric label="Trading" value="Paper / Live off" tone="green" />
      <Metric label="Discovery" value={scanner.poolLimitReason ?? (d.controls?.isPaused ? 'Paused' : 'Scanning')} tone={d.controls?.isPaused ? 'yellow' : 'cyan'} />
      <Metric label="Readiness" value={d.controls?.isPaused ? 'Paused' : 'Ready'} tone={d.controls?.isPaused ? 'yellow' : 'green'} />
      <Metric label="Equity" value={money(equity)} tone="green" />
      <Metric label="Cash" value={money(cash)} tone="green" />
      <Metric label="P/L" value={money(pnl)} tone={pnl < 0 ? 'red' : 'green'} />
      <Metric label="Heartbeat" value={time(d.lastHeartbeat)} tone="muted" />
    </header>

    <main className="clean-dashboard">
      <Panel title="P/L Vector" active>
        <div className="pl-summary"><BigStat label="Equity" value={money(equity)} tone="green" /><BigStat label="Realized P/L" value={money(pnl)} tone={pnl < 0 ? 'red' : 'green'} /><BigStat label="Locked" value={money(d.status?.lockedCapital ?? paper.locked ?? 0)} tone="yellow" /><BigStat label="Open Positions" value={openPositions.length} tone="cyan" /></div>
        <div className="pl-chart">{d.equity.length ? <ResponsiveContainer><AreaChart data={d.equity} margin={{ top: 14, right: 18, left: 4, bottom: 6 }}><defs><linearGradient id="plGlow" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="#35ff9c" stopOpacity={0.42} /><stop offset="100%" stopColor="#35ff9c" stopOpacity={0.02} /></linearGradient></defs><CartesianGrid stroke="rgba(53,255,156,.08)" vertical={false} /><XAxis dataKey="timestamp" hide /><YAxis tick={{ fill: '#6ee7b7', fontSize: 10 }} width={58} /><Tooltip contentStyle={{ background: '#050807', border: '1px solid rgba(53,255,156,.35)', color: '#d9fff1' }} /><Area type="monotone" dataKey="equity" stroke="#35ff9c" strokeWidth={3} fill="url(#plGlow)" /></AreaChart></ResponsiveContainer> : <div className="chart-empty"><span>No P/L points yet</span><small>Waiting for portfolio/equity updates</small></div>}</div>
      </Panel>

      <section className="operations-grid">
        <Panel title="Recent Trades" active compact>{d.trades.length ? <Table t={tradeTable} /> : <Empty label="No recent trades" />}</Panel>
        <Panel title="Open Positions" active compact>{openPositions.length ? <Table t={positionTable} /> : <Empty label="No open positions" />}</Panel>
      </section>

      <Panel title={`Fatal Errors${fatalLogs.length ? ` (${fatalLogs.length})` : ''}`} active={fatalLogs.length > 0} compact>
        <div ref={fatalRef} className="fatal-console">{fatalLogs.length ? fatalLogs.map((l: any) => <div key={l.id} className="fatal-line"><span>{time(l.timestamp)}</span><b>{l.source ?? 'runtime'}</b><p>{l.message}</p></div>) : <Empty label="No fatal errors" />}{d.connectionStatus !== 'connected' && <div className="fatal-line connection"><span>{time(d.lastUpdated)}</span><b>connection</b><p>Backend connection is {d.connectionStatus}</p></div>}</div>
      </Panel>

      <section className="diagnostics-shell">
        <button className="diagnostics-toggle" onClick={() => setShowDiagnostics((v) => !v)}>{showDiagnostics ? 'Hide diagnostics' : 'Show diagnostics'}</button>
        {showDiagnostics && <div className="diagnostics-grid"><MiniBlock title="Scanner" rows={[['Active', scanner.activeMarketsAvailable ?? scanner.activeMarketsDiscovered ?? 0], ['Pool', scanner.effectiveMarketPoolSize ?? scanner.effectiveMarketLimit ?? 0], ['Candidates', scanner.candidatesEvaluated ?? 0], ['Liquidity pass', scanner.marketsPassingLiquidity ?? 0], ['Best edge', scanner.bestEdgeIsAvailable ? scanner.bestEdgeSeen : 'N/A']]} /><MiniBlock title="Paper Summary" rows={[['Exposure', money(paper.totalExposure ?? d.status?.lockedCapital ?? 0)], ['Settlements', d.paperSettlements?.length ?? paper.settlements ?? 0], ['Rejects', Object.entries(paper.blockedCountsByReason ?? {}).map(([k, v]: any) => `${k}=${v}`).join(' ') || '-']]} /><MiniBlock title="Runtime" rows={[['Source', d.source], ['REST error', d.lastRestError || '-'], ['Last event', d.lastEvent || '-'], ['Updated', time(d.lastUpdated)], ['Logs', d.logs.length]]} /></div>}
      </section>
    </main>
  </div>;
}

function Metric({ label, value, tone }: any) { return <div className="status-metric"><span>{label}</span><strong className={`tone-${tone ?? 'green'}`}>{value}</strong></div>; }
function BigStat({ label, value, tone }: any) { return <div className="big-stat"><span>{label}</span><strong className={`tone-${tone ?? 'green'}`}>{value}</strong></div>; }
function MiniBlock({ title, rows }: any) { return <div className="mini-block"><h4>{title}</h4>{rows.map(([k, v]: any) => <div key={k} className="mini-row"><span>{k}</span><strong>{v}</strong></div>)}</div>; }
function Empty({ label }: any) { return <div className="empty-state">{label}</div>; }
function Table({ t }: any) { return <div className="table-wrap"><table className="terminal-table"><thead>{t.getHeaderGroups().map((hg: any) => <tr key={hg.id}>{hg.headers.map((h: any) => <th key={h.id}>{flexRender(h.column.columnDef.header, h.getContext())}</th>)}</tr>)}</thead><tbody>{t.getRowModel().rows.map((r: any) => <tr key={r.id}>{r.getVisibleCells().map((c: any) => <td key={c.id}>{flexRender(c.column.columnDef.cell, c.getContext())}</td>)}</tr>)}</tbody></table></div>; }
