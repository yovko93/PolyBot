import { useEffect, useMemo, useState } from 'react';
import { createColumnHelper, flexRender, getCoreRowModel, useReactTable } from '@tanstack/react-table';
import { Area, AreaChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { equitySeries, riskState, terminalLogs } from './data/mockData';
import { getBotStatus, getOpportunities, getPositions, getScannerStats, getTradeLogs, subscribeToBotEvents } from './services/botApi';
import type { BotStatus, Opportunity, PaperPosition, ScannerStats, TradeLogEntry } from './types/models';

const money = (n: number) => `$${n.toFixed(2)}`;
const Panel = ({ title, children }: { title: string; children: React.ReactNode }) => <section className="panel p-2"><h3 className="terminal-font text-xs text-neon mb-2">{title}</h3>{children}</section>;

export default function App() {
  const [status, setStatus] = useState<BotStatus | null>(null); const [ops, setOps] = useState<Opportunity[]>([]); const [pos, setPos] = useState<PaperPosition[]>([]); const [logs, setLogs] = useState<TradeLogEntry[]>([]); const [stats, setStats] = useState<ScannerStats | null>(null); const [live, setLive] = useState<string[]>(terminalLogs);
  useEffect(() => { void Promise.all([getBotStatus().then(setStatus), getOpportunities().then(setOps), getPositions().then(setPos), getTradeLogs().then(setLogs), getScannerStats().then(setStats)]); const unsub = subscribeToBotEvents((e) => setLive((x) => [e, ...x].slice(0, 14))); return unsub; }, []);
  const opCols = useMemo(() => { const c = createColumnHelper<Opportunity>(); return ['rank','strategy','group','market','side','edgePerShare','expectedProfit','costOrProceeds','guaranteedPayout','qtyAvailable','executable','status'].map((k)=>c.accessor(k as keyof Opportunity,{header:k,cell:(i)=>String(i.getValue())})); }, []);
  const tradeCols = useMemo(() => { const c = createColumnHelper<TradeLogEntry>(); return ['time','strategy','side','market','amount','price','edge','status'].map((k)=>c.accessor(k as keyof TradeLogEntry,{header:k,cell:(i)=>String(i.getValue())})); }, []);
  const posCols = useMemo(() => { const c = createColumnHelper<PaperPosition>(); return ['market','side','qty','avgPrice','mark','unrealizedPnl','status'].map((k)=>c.accessor(k as keyof PaperPosition,{header:k,cell:(i)=>String(i.getValue())})); }, []);
  const opTable = useReactTable({ data: ops, columns: opCols, getCoreRowModel: getCoreRowModel() });
  const tradeTable = useReactTable({ data: logs, columns: tradeCols, getCoreRowModel: getCoreRowModel() });
  const posTable = useReactTable({ data: pos, columns: posCols, getCoreRowModel: getCoreRowModel() });

  return <div className="h-screen bg-black text-green-200 p-2 grid grid-cols-[180px_1fr_320px] gap-2 terminal-font text-xs">
    <aside className="panel p-2 space-y-2"><div className="text-neon font-bold">POLYBOT TERMINAL</div>{['Dashboard','Opportunities','Positions','Trade Log','Risk','Settings'].map(i=><div key={i} className="border border-green-700/40 p-1 rounded">{i}</div>)}<Panel title="Strategies"><div>BUY_YES_AND_BUY_NO ✅</div><div>BUY_ALL_NO_MUTUALLY_EXCLUSIVE ✅</div><div>MINT_AND_SELL_YES_NO ⚠</div></Panel></aside>
    <main className="space-y-2 overflow-auto">
      <section className="panel p-2 grid grid-cols-7 gap-2">{status && <><div>Mode <span className="text-neon">{status.mode}</span></div><div>Conn {status.connection}</div><div>Cash {money(status.cash)}</div><div>Locked {money(status.lockedCapital)}</div><div>Equity {money(status.equity)}</div><div>Realized {money(status.realizedPnl)}</div><div>Last {new Date(status.lastScanTime).toLocaleTimeString()}</div></>}</section>
      <Panel title="Equity Curve"><div className="h-48"><ResponsiveContainer><AreaChart data={equitySeries}><XAxis dataKey="t" stroke="#39ff14"/><YAxis stroke="#39ff14"/><Tooltip/><Area dataKey="equity" stroke="#39ff14" fill="#39ff1444"/></AreaChart></ResponsiveContainer></div></Panel>
      <Panel title="Opportunity Ranking"><Table t={opTable} /></Panel>
      <div className="grid grid-cols-2 gap-2"><Panel title="Scanner Stats">{stats && <div className='space-y-1'><div>Scans/min: {stats.scansPerMin}</div><div>Tracked: {stats.marketsTracked}</div><div>Executable rate: {(stats.executableRate*100).toFixed(1)}%</div></div>}</Panel><Panel title="Active Positions"><Table t={posTable} /></Panel></div>
      <Panel title="Trade Log"><Table t={tradeTable} /></Panel>
    </main>
    <aside className="space-y-2 overflow-auto"><Panel title="Risk Limits"><div className="space-y-1"><div>Max Locked: {money(riskState.maxLockedCapital)}</div><div className="text-orange-300">Utilization: {riskState.utilizationPct}%</div><div className="text-red-400">Daily Loss Limit: {money(riskState.dailyLossLimit)}</div></div></Panel><Panel title="Execution Mode">DRY RUN / PAPER / LIVE</Panel><Panel title="Simulated Resolution">Placeholder controls</Panel><Panel title="Terminal Log"><div className="space-y-1 max-h-72 overflow-auto">{live.map((l)=><div key={l}>{l}</div>)}</div></Panel></aside>
  </div>;
}

function Table<T>({ t }: { t: ReturnType<typeof useReactTable<T>> }) { return <div className="overflow-auto"><table className="w-full text-[10px]"><thead>{t.getHeaderGroups().map(hg=><tr key={hg.id}>{hg.headers.map(h=><th key={h.id} className="text-neon text-left pr-2">{flexRender(h.column.columnDef.header, h.getContext())}</th>)}</tr>)}</thead><tbody>{t.getRowModel().rows.map(r=><tr key={r.id} className="border-t border-green-800/30">{r.getVisibleCells().map(c=><td key={c.id} className="pr-2 py-1">{flexRender(c.column.columnDef.cell, c.getContext())}</td>)}</tr>)}</tbody></table></div>; }
