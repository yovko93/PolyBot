import { useEffect, useMemo, useState } from 'react';
import { createColumnHelper, flexRender, getCoreRowModel, useReactTable } from '@tanstack/react-table';
import { Area, AreaChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { equitySeries, riskState, terminalLogs } from './data/mockData';
import { getBotStatus, getOpportunities, getPositions, getScannerStats, getTradeLogs, subscribeToBotEvents } from './services/botApi';
import type { BotStatus, Opportunity, PaperPosition, ScannerStats, TradeLogEntry } from './types/models';

const money = (n: number) => `$${n.toFixed(2)}`;
const fmt = (n: number) => n.toFixed(4);
const timestampUtc = () => new Date().toISOString().replace('T', ' ').slice(0, 19) + ' UTC';

function Panel({ title, children, active }: { title: string; children: React.ReactNode; active?: boolean }) {
  return (
    <section className={`terminal-panel ${active ? 'terminal-panel-active' : ''}`}>
      <h3 className="panel-title">{title}</h3>
      {children}
    </section>
  );
}

export default function App() {
  const [status, setStatus] = useState<BotStatus | null>(null);
  const [ops, setOps] = useState<Opportunity[]>([]);
  const [pos, setPos] = useState<PaperPosition[]>([]);
  const [logs, setLogs] = useState<TradeLogEntry[]>([]);
  const [stats, setStats] = useState<ScannerStats | null>(null);
  const [live, setLive] = useState<string[]>(terminalLogs);

  useEffect(() => {
    void Promise.all([
      getBotStatus().then(setStatus),
      getOpportunities().then(setOps),
      getPositions().then(setPos),
      getTradeLogs().then(setLogs),
      getScannerStats().then(setStats)
    ]);
    const unsub = subscribeToBotEvents((e) => setLive((x) => [e, ...x].slice(0, 16)));
    const interval = window.setInterval(() => setClock(timestampUtc()), 1000);
    return () => {
      unsub();
      window.clearInterval(interval);
    };
  }, []);

  const [clock, setClock] = useState(timestampUtc());

  const opCols = useMemo(() => {
    const c = createColumnHelper<Opportunity>();
    return [
      'rank', 'strategy', 'group', 'market', 'side', 'edgePerShare', 'expectedProfit', 'qtyAvailable', 'executable', 'status'
    ].map((k) => c.accessor(k as keyof Opportunity, { header: k.toUpperCase(), cell: (i) => String(i.getValue()) }));
  }, []);

  const tradeCols = useMemo(() => {
    const c = createColumnHelper<TradeLogEntry>();
    return ['time', 'strategy', 'side', 'market', 'amount', 'price', 'edge', 'status']
      .map((k) => c.accessor(k as keyof TradeLogEntry, { header: k.toUpperCase(), cell: (i) => String(i.getValue()) }));
  }, []);

  const posCols = useMemo(() => {
    const c = createColumnHelper<PaperPosition>();
    return ['market', 'side', 'qty', 'avgPrice', 'mark', 'unrealizedPnl', 'status']
      .map((k) => c.accessor(k as keyof PaperPosition, { header: k.toUpperCase(), cell: (i) => String(i.getValue()) }));
  }, []);

  const opTable = useReactTable({ data: ops, columns: opCols, getCoreRowModel: getCoreRowModel() });
  const tradeTable = useReactTable({ data: logs, columns: tradeCols, getCoreRowModel: getCoreRowModel() });
  const posTable = useReactTable({ data: pos, columns: posCols, getCoreRowModel: getCoreRowModel() });

  const signalsCount = stats?.opportunitiesLastScan ?? ops.length;

  return (
    <div className="terminal-root min-h-screen px-3 py-2 text-[11px] text-green-100 terminal-font">
      <header className="terminal-panel terminal-panel-active mb-2 p-2">
        <div className="grid grid-cols-1 xl:grid-cols-[1.1fr_2fr_1.4fr] gap-2 items-center">
          <div className="text-neon tracking-widest font-semibold text-sm">POLYBOT TERMINAL</div>
          <div className="grid grid-cols-5 gap-x-2 gap-y-1 text-[10px]">
            <Metric label="SCANNER" value="ACTIVE" tone="green" />
            <Metric label="MODE" value={status?.mode ?? 'DRY RUN'} tone="yellow" />
            <Metric label="SIGNALS" value={String(signalsCount)} tone="green" />
            <Metric label="CONN" value={status?.connection ?? 'CONNECTED'} tone="green" />
            <Metric label="UTC" value={clock} tone="neutral" />
          </div>
          <div className="grid grid-cols-4 gap-2 text-[10px]">
            <Metric label="CASH" value={money(status?.cash ?? 1000)} tone="green" />
            <Metric label="LOCKED" value={money(status?.lockedCapital ?? 0)} tone="yellow" />
            <Metric label="EQUITY" value={money(status?.equity ?? 1000)} tone="green" />
            <Metric label="PNL" value={money(status?.realizedPnl ?? 0)} tone={(status?.realizedPnl ?? 0) >= 0 ? 'green' : 'red'} />
          </div>
        </div>
      </header>

      <div className="grid gap-2 xl:grid-cols-[280px_1fr_320px] items-start">
        <aside className="space-y-2">
          <Panel title="RISK CONSOLE" active>
            <div className="space-y-1 text-[10px]">
              <div>MAX LOCKED: <span className="text-neon">{money(riskState.maxLockedCapital)}</span></div>
              <div>UTILIZATION: <span className="text-yellow-300">{riskState.utilizationPct}%</span></div>
              <div>DAILY LOSS LIMIT: <span className="text-rose-400">{money(riskState.dailyLossLimit)}</span></div>
              <div>LOCKED NOW: <span className="text-yellow-300">{money(status?.lockedCapital ?? 0)}</span></div>
            </div>
          </Panel>
          <Panel title="ARBITRAGE SCANNER">
            <div className="grid grid-cols-2 gap-1 text-[10px]">
              <Stat label="SCANS/MIN" value={String(stats?.scansPerMin ?? 0)} />
              <Stat label="TRACKED" value={String(stats?.marketsTracked ?? 0)} />
              <Stat label="LAST SIGNALS" value={String(stats?.opportunitiesLastScan ?? 0)} />
              <Stat label="EXEC RATE" value={`${((stats?.executableRate ?? 0) * 100).toFixed(1)}%`} />
            </div>
          </Panel>
          <Panel title="STRATEGY STATUS">
            <div className="space-y-1 text-[10px]">
              <div className="text-neon">SingleMarketBuyBoth | BUY_YES_AND_BUY_NO</div>
              <div className="text-neon">MultiOutcomeGroup | BUY_ALL_NO_MUTUALLY_EXCLUSIVE</div>
              <div className="text-yellow-300">CompleteSetSell | MINT_AND_SELL_YES_NO</div>
            </div>
          </Panel>
        </aside>

        <main className="space-y-2">
          <Panel title="MARKET SCANNER" active>
            <Table t={opTable} />
          </Panel>
          <Panel title="OPPORTUNITY SIGNALS">
            <div className="h-40">
              <ResponsiveContainer>
                <AreaChart data={equitySeries}>
                  <XAxis dataKey="t" stroke="#39ff14" tick={{ fontSize: 9 }} />
                  <YAxis stroke="#39ff14" tick={{ fontSize: 9 }} />
                  <Tooltip contentStyle={{ background: '#071007', border: '1px solid #39ff14' }} />
                  <Area dataKey="equity" stroke="#39ff14" fill="#39ff1418" />
                </AreaChart>
              </ResponsiveContainer>
            </div>
          </Panel>
          <Panel title="TRADE LOG">
            <Table t={tradeTable} />
          </Panel>
          <Panel title="PAPER POSITIONS">
            <Table t={posTable} />
          </Panel>
        </main>

        <aside className="space-y-2">
          <Panel title="AI CONFIDENCE SCORER" active>
            <div className="space-y-1 text-[10px]">
              <div>MODEL CONFIDENCE <span className="text-neon">91.4%</span></div>
              <div>RISK FILTER <span className="text-yellow-300">DRY RUN</span></div>
              <div>SLIPPAGE REGIME <span className="text-rose-300">NO FILL RISK: MED</span></div>
              <div>EDGE/SHARE FLOOR <span className="text-neon">{fmt(0.002)}</span></div>
            </div>
          </Panel>
          <Panel title="LIVE TERMINAL LOG">
            <div className="space-y-1 max-h-[430px] overflow-auto text-[10px] pr-1">
              {live.map((l) => <div key={l} className="terminal-line">{l}</div>)}
              <div className="text-neon">bot@polybot:~$ <span className="cursor-block">█</span></div>
            </div>
          </Panel>
        </aside>
      </div>
      <footer className="mt-2 text-[10px] text-green-500/70">[system] DRY RUN ENABLED · no real orders routed · Ctrl+C to exit</footer>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return <div className="border border-green-500/30 px-1 py-[2px]"><span className="text-green-500/80">{label}</span> <span className="text-neon">{value}</span></div>;
}

function Metric({ label, value, tone }: { label: string; value: string; tone: 'green' | 'yellow' | 'red' | 'neutral' }) {
  const toneClass = tone === 'green' ? 'text-neon' : tone === 'yellow' ? 'text-yellow-300' : tone === 'red' ? 'text-rose-400' : 'text-green-200';
  return <div><span className="text-green-500/80">{label}:</span> <span className={toneClass}>{value}</span></div>;
}

function Table<T>({ t }: { t: ReturnType<typeof useReactTable<T>> }) {
  return (
    <div className="overflow-auto">
      <table className="w-full terminal-table">
        <thead>
          {t.getHeaderGroups().map((hg) => (
            <tr key={hg.id}>
              {hg.headers.map((h) => (
                <th key={h.id} className="text-left">{flexRender(h.column.columnDef.header, h.getContext())}</th>
              ))}
            </tr>
          ))}
        </thead>
        <tbody>
          {t.getRowModel().rows.map((r) => (
            <tr key={r.id}>
              {r.getVisibleCells().map((c) => {
                const value = String(c.getValue() ?? '');
                return <td key={c.id} className={statusClass(value)}>{flexRender(c.column.columnDef.cell, c.getContext())}</td>;
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function statusClass(value: string) {
  if (value.includes('YES') || value === 'true' || value.includes('EXECUTABLE') || value.includes('FILLED')) return 'text-neon';
  if (value.includes('NO') || value.includes('REJECTED')) return 'text-rose-300';
  if (value.includes('DRY RUN') || value.includes('SKIP') || value.includes('WATCH')) return 'text-yellow-300';
  return '';
}
