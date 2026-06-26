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
  'paper execution error', 'paper execution failure', 'non-singlemarket paper'
];

function isRuntimeExportFileLock(log: any) {
  const raw = `${log.level ?? ''} ${log.source ?? ''} ${log.message ?? ''}`.toLowerCase();
  return raw.includes('runtime-soak-status-latest.json') || raw.includes('runtime_status_export') || raw.includes('runtime status export');
}

function shortMessage(message: string) {
  const withoutPayload = message.replace(/\{[\s\S]*$/, '').replace(/\[[^\]]+\]\s*/, '').trim();
  return (withoutPayload || message).slice(0, 180);
}

function classifyAlert(log: any) {
  const raw = `${log.level ?? ''} ${log.source ?? ''} ${log.message ?? ''}`;
  const lower = raw.toLowerCase();
  if (isRuntimeExportFileLock(log)) return null;
  if (normalDiagnosticPatterns.some((p) => lower.includes(p))) return null;
  if (lower.includes('abort')) return null;
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
  const runtimeExportStable = runtime(health, 'runtimeStatusExportStable') !== false;
  const runtimeExportWriteFailures = Number(runtime(health, 'runtimeStatusExportWriteFailures') ?? 0);
  const runtimeExportReadFailures = Number(runtime(health, 'runtimeStatusExportReadFailures') ?? 0);
  const runtimeExportRecoveredCount = Number(runtime(health, 'runtimeStatusExportRecoveredCount') ?? 0);
  const runtimeExportFailureReason = String(runtime(health, 'runtimeStatusExportLastFailureReason') ?? '');
  const runtimeExportAlert = !runtimeExportStable && (runtimeExportWriteFailures + runtimeExportReadFailures) >= 3 && runtimeExportRecoveredCount === 0
    ? { id: 'runtime-export-lock', timestamp: d.lastUpdated, severity: 'WARNING', source: 'Runtime Export', message: shortMessage(runtimeExportFailureReason || 'Runtime status export file lock is repeated and unrecovered') }
    : null;
  const logAlerts = useMemo(() => d.logs.map(classifyAlert).filter(Boolean).slice(0, 20), [d.logs]);
  const connectionAlert = !backendConnected ? { id: 'connection', timestamp: d.lastUpdated, severity: 'WARNING', source: 'Connection', message: `Backend connection is ${d.connectionStatus}` } : null;
  const systemAlerts = [connectionAlert, runtimeExportAlert, ...logAlerts].filter(Boolean);
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
  const currentEquityValue = first(runtime(health, 'paperEquity'), d.status?.equity, paper.equity);
  const currentCashValue = first(runtime(health, 'paperCash'), d.status?.cash, paper.cash);
  const equity = Number(currentEquityValue ?? 0);
  const cash = Number(currentCashValue ?? 0);
  const pnl = Number(first(runtime(health, 'paperRealizedPnl'), d.status?.realizedPnl, paper.realizedPnl, 0));
  const locked = Number(first(runtime(health, 'paperLocked'), d.status?.lockedCapital, paper.locked, 0));
  const equityHistory = (d.equity ?? []).filter((p: any) => Number.isFinite(Number(p.equity)));
  const hasCurrentEquity = Number.isFinite(Number(currentEquityValue)) || Number.isFinite(Number(currentCashValue));
  const chartData = equityHistory.length ? equityHistory : (hasCurrentEquity ? [{ timestamp: d.lastUpdated || new Date().toISOString(), equity }] : []);
  const equityValues = chartData.map((p: any) => Number(p.equity)).filter(Number.isFinite);
  const chartHasData = equityValues.length > 0;
  const minEquity = chartHasData ? Math.min(...equityValues) : 0;
  const maxEquity = chartHasData ? Math.max(...equityValues) : 0;
  const flatEquity = chartHasData && minEquity === maxEquity;
  const singleEquityPoint = equityValues.length === 1;
  const domainPadding = flatEquity ? Math.max(5, Math.abs(minEquity) * 0.01) : Math.max(1, (maxEquity - minEquity) * 0.12);
  const chartDomain: [number, number] = [minEquity - domainPadding, maxEquity + domainPadding];

  const strategyCounters = strategyCounterMap(health);
  const singleStrategy = getStrategyCounter(strategyCounters, 'SingleMarketBuyBoth');
  const autoStrategy = getStrategyCounter(strategyCounters, 'AutoCandidateMultiOutcome');
  const verifiedStrategy = getStrategyCounter(strategyCounters, 'VerifiedMultiOutcome');
  const nearMissStrategy = getStrategyCounter(strategyCounters, 'MultiOutcomeNearMiss');
  const activeStrategies = Object.entries(strategyCounters).filter(([, c]: any) => String(c?.mode ?? '').toLowerCase() !== 'disabled' && Number(c?.scan ?? 0) > 0);
  const nonSinglePaperAttempt = Object.entries(strategyCounters).some(([name, c]: any) => name.toLowerCase() !== 'singlemarketbuyboth' && Number(c?.paper ?? c?.paperOpened ?? 0) > 0);
  const singleCycle = runtime(health, 'singleMarketFullCycleSummary') ?? {};
  const reducedMarkets = runtime(health, 'reducedUniverseMarkets') ?? scanner.effectiveMarketPoolSize ?? scanner.effectiveMarketLimit ?? 0;
  const discoveryMode = String(first(runtime(health, 'discoveryMode'), runtime(health, 'discoverySelectedSource'), '')).toLowerCase();
  const backendPaperDiagnosticsEligible = runtime(health, 'paperDiagnosticsLimitedEligible') === true;
  const paperDiagnosticsEnabled = runtime(health, 'paperDiagnosticsLimitedEnabled') === true;
  const paperDiagnosticsEligibleSafe = backendPaperDiagnosticsEligible
    && discoveryMode === 'reduceduniversediagnosticsonly'
    && runtime(health, 'discoveryReducedUniverse') === true
    && String(runtime(health, 'diagnosticsUniverse') ?? '').toLowerCase() === 'reduced';
  const backendPaperDiagnosticsBlockedReason = String(runtime(health, 'paperDiagnosticsLimitedBlockedReason') ?? '');
  const paperDiagnosticsBlockedReason = paperDiagnosticsEligibleSafe
    ? 'None'
    : (backendPaperDiagnosticsEligible && !paperDiagnosticsEligibleSafe && (!backendPaperDiagnosticsBlockedReason || backendPaperDiagnosticsBlockedReason === 'None')
      ? 'ReducedUniverseNotActive'
      : (backendPaperDiagnosticsBlockedReason || 'NotEligible'));
  const tradingState = paperDiagnosticsEnabled
    ? 'Paper limited'
    : (runtime(health, 'strategyExecutionGloballyBlocked') ? 'Strategy blocked' : 'Paper / Live off');
  const singleCandidates = first(singleStrategy?.cand, scanner.candidatesEvaluated, 0);
  const bestEdge = first(singleCycle.bestEdge, scanner.bestEdgeIsAvailable ? scanner.bestEdgeSeen : undefined, 'N/A');
  const scannerRows = [
    ['Universe', runtime(health, 'discoveryReducedUniverse') ? 'Reduced' : (runtime(health, 'diagnosticsUniverse') ?? 'Full')],
    ['Pool', reducedMarkets],
    ['Single scan', first(singleStrategy?.scan, scanner.marketsScannedThisCycle, scanner.marketsScanned, 0)],
    ['Books', first(singleStrategy?.books, scanner.orderbooksScanned, scanner.orderbooksRequested, 0)],
    ['Candidates', singleCandidates],
    ['Positive', first(singleStrategy?.positive, scanner.positiveEdgeFound, 0)],
    ['Paper opened', first(singleStrategy?.paper, runtime(health, 'paperOpened'), 0)],
    ['Best edge', bestEdge],
    ['Best raw edge', first(runtime(health, 'singleMarketBestRawEdge'), 'N/A')],
    ['Best after-cost edge', first(runtime(health, 'singleMarketBestAfterCostEdge'), 'N/A')],
    ['Best after-safety edge', first(runtime(health, 'singleMarketBestAfterSafetyEdge'), 'N/A')],
    ['Positive before cost', first(runtime(health, 'singleMarketPositiveBeforeCost'), 0)],
    ['Positive after cost', first(runtime(health, 'singleMarketPositiveAfterCost'), 0)],
    ['Positive after safety', first(runtime(health, 'singleMarketPositiveAfterSafety'), 0)],
    ['Execution ready', first(runtime(health, 'singleMarketExecutionReady'), singleStrategy?.ready, 0)],
    ['Best rejected reason', first(runtime(health, 'singleMarketBestRejectedReason'), 'None')]
  ];
  const orderbookRows = [
    ['Stable', String(runtime(health, 'reducedUniverseOrderbookStable') ?? true)],
    ['Eligible', runtime(health, 'reducedUniverseOrderbookEligibleMarkets') ?? '-'],
    ['Paused', String(runtime(health, 'reducedUniverseScanPausedByOrderbookHealth') ?? false)],
    ['Invalid quarantine', runtime(health, 'invalidTokenQuarantineActive') ?? 0],
    ['Market quarantine', runtime(health, 'marketOrderbookQuarantineActive') ?? 0],
    ['Breaker', runtime(health, 'orderbookCircuitBreakerState') ?? '-'],
    ['Breaker opens', runtime(health, 'orderbookCircuitBreakerOpenCount') ?? 0],
    ['Lifecycle', `${runtime(health, 'marketOrderbookQuarantineLifecycleBalanced') ?? true}/${runtime(health, 'invalidTokenQuarantineLifecycleBalanced') ?? true}`],
    ['Stable now', String(runtime(health, 'orderbookStableNow') ?? true)],
    ['True post bad', runtime(health, 'truePostBreakerBadRequests') ?? 0],
    ['In-flight bad', runtime(health, 'inFlightBeforeBreakerBadRequestsAfterOpen') ?? 0],
    ['Bad history', `${runtime(health, 'reducedUniverseBadHistoryActive') ?? 0}/${runtime(health, 'reducedUniverseBadHistoryExpired') ?? 0}`],
    ['Paper diag eligible', String(paperDiagnosticsEligibleSafe)],
    ['Paper diag reason', paperDiagnosticsBlockedReason],
    ...(backendPaperDiagnosticsEligible && !paperDiagnosticsEligibleSafe ? [['Paper diag warning', 'Backend eligible ignored: not ReducedUniverseDiagnosticsOnly']] : [])
  ];
  const strategyRows = [
    ['Status', activeStrategies.length > 1 ? 'Multi-strategy diagnostics active' : 'Single strategy'],
    ...(nonSinglePaperAttempt ? [['Warning', 'Non-SingleMarket paper attempt detected']] : []),
    ['SingleMarketBuyBoth', strategyCompact(singleStrategy, ['scan', 'books', 'cand', 'positive', 'ready', 'shadow', 'paper'])],
    ['AutoCandidate', strategyCompact(autoStrategy, ['scan', 'books', 'cand', 'positive', 'ready', 'shadow', 'paper'])],
    ['Verified', strategyCompact(verifiedStrategy, ['scan', 'books', 'cand', 'positive', 'ready', 'shadow', 'paper'])],
    ['MultiOutcomeNearMiss', strategyCompact(nearMissStrategy, ['scan', 'books', 'cand', 'positive', 'ready', 'shadow', 'paper'])]
  ];
  const strategyTableRows = Object.entries(strategyCounters).map(([name, c]: any) => ({
    strategy: name, mode: c?.mode ?? '-', scan: c?.scan ?? 0, books: c?.books ?? 0, candidates: c?.cand ?? 0, positive: c?.positive ?? 0, ready: c?.ready ?? 0, shadow: c?.shadow ?? c?.shadowWouldOpen ?? 0, paper: c?.paper ?? 0, bestValid: (c?.bestCandidateValid && c?.bestCandidatePriced) ? (c?.bestValidPricedAfterSafetyEdge ?? c?.bestAfterSafetyEdge ?? '-') : '-', bestExec: (Number(c?.ready ?? 0) > 0 || Number(c?.shadow ?? c?.shadowWouldOpen ?? 0) > 0) && c?.bestCandidateValid ? (c?.bestExecutableAfterSafetyEdge ?? c?.bestAfterSafetyEdge ?? '-') : '-', badge: !c?.bestCandidatePriced ? 'Missing pricing' : !c?.bestCandidateValid ? (c?.bestCandidateReason ?? 'Invalid') : '', reason: c?.bestCandidateReason ?? c?.bestRejectedReason ?? c?.topSkipReason ?? '-'
  }));

  const strategyTable = useReactTable({
    data: strategyTableRows,
    columns: [
      column.accessor('strategy', { header: 'Strategy', cell: (i) => text(i.getValue()) }),
      column.accessor('mode', { header: 'Mode', cell: (i) => text(i.getValue()) }),
      column.accessor('scan', { header: 'Scan', cell: (i) => text(i.getValue()) }),
      column.accessor('books', { header: 'Books', cell: (i) => text(i.getValue()) }),
      column.accessor('candidates', { header: 'Candidates', cell: (i) => text(i.getValue()) }),
      column.accessor('positive', { header: 'Positive', cell: (i) => text(i.getValue()) }),
      column.accessor('ready', { header: 'Execution ready', cell: (i) => text(i.getValue()) }),
      column.accessor('shadow', { header: 'Shadow would open', cell: (i) => text(i.getValue()) }),
      column.accessor('paper', { header: 'Paper opened', cell: (i) => text(i.getValue()) }),
      column.accessor('bestValid', { header: 'Best valid edge', cell: (i) => text(i.getValue()) }),
      column.accessor('bestExec', { header: 'Best executable edge', cell: (i) => text(i.getValue()) }),
      column.accessor('badge', { header: 'Validity', cell: (i) => text(i.getValue() || 'Valid priced') }),
      column.accessor('reason', { header: 'Last blocked reason', cell: (i) => text(i.getValue()) })
    ],
    getCoreRowModel: getCoreRowModel()
  });

  const edgeDistributionRows = [
    ['Samples', runtime(health, 'singleMarketValidEdgeSamples') ?? 0],
    ['Raw P95 / P99 / Max', `${first(runtime(health, 'singleMarketRawEdgeP95'), 'N/A')} / ${first(runtime(health, 'singleMarketRawEdgeP99'), 'N/A')} / ${first(runtime(health, 'singleMarketRawEdgeMax'), 'N/A')}`],
    ['After-safety P95 / P99 / Max', `${first(runtime(health, 'singleMarketAfterSafetyEdgeP95'), 'N/A')} / ${first(runtime(health, 'singleMarketAfterSafetyEdgeP99'), 'N/A')} / ${first(runtime(health, 'singleMarketAfterSafetyEdgeMax'), 'N/A')}`],
    ['Count -1bp..0', runtime(health, 'singleMarketAfterSafetyEdgeMinus1bpTo0') ?? 0],
    ['Count 0..1bp', runtime(health, 'singleMarketAfterSafetyEdge0To1bp') ?? 0],
    ['Count >5bp', runtime(health, 'singleMarketAfterSafetyEdgeAbove5bp') ?? 0]
  ];
  const cycleRows = singleCycle && Object.keys(singleCycle).length ? [
    ['Cycle', singleCycle.cycle ?? '-'],
    ['Markets', singleCycle.markets ?? '-'],
    ['Data quality', singleCycle.dataQualityRejected ?? '-'],
    ['Below min edge', singleCycle.belowMinEdge ?? '-'],
    ['Positive edge', singleCycle.positiveEdge ?? '-'],
    ['Best edge', singleCycle.bestEdge ?? '-'],
    ['Best rejected', singleCycle.bestRejectedRawEdge ?? '-']
  ] : [];

  return <div className="terminal-root min-h-screen terminal-font">
    <header className="status-strip" aria-label="Trading bot status">
      <div className="brand-chip">POLYBOT</div>
      <Metric label="Backend" value={connectionLabel} tone={backendConnected ? 'green' : 'yellow'} />
      <Metric label="Mode" value={d.status?.mode ?? `PaperPhase ${runtime(health, 'paperPhase') ?? 'PaperOnly'}`} tone="cyan" />
      <Metric label="Trading" value={tradingState} tone={tradingState === 'Paper limited' ? 'cyan' : tradingState === 'Strategy blocked' ? 'yellow' : 'green'} />
      <Metric label="Discovery" value={backendConnected ? discoveryLabel(health, scanner, d.controls) : 'Waiting'} tone={backendConnected ? (d.controls?.isPaused || runtime(health, 'discoveryReducedUniverse') ? 'yellow' : 'cyan') : 'yellow'} />
      <Metric label="Readiness" value={backendConnected ? readinessLabel(health, d.controls) : 'No backend'} tone={backendConnected ? (runtime(health, 'tradingReadiness') ? 'green' : 'yellow') : 'yellow'} />
      <Metric label="P/L" value={money(pnl)} tone={pnl < 0 ? 'red' : 'green'} />
    </header>

    <main className="clean-dashboard">
      <Panel title="P/L Vector" active>
        <div className="pl-summary"><BigStat label="Equity" value={money(equity)} tone="green" /><BigStat label="Cash" value={money(cash)} tone="green" /><BigStat label="Realized P/L" value={money(pnl)} tone={pnl < 0 ? 'red' : 'green'} /><BigStat label="Heartbeat" value={time(d.lastHeartbeat)} tone="muted" /><BigStat label="Locked" value={money(locked)} tone="yellow" /><BigStat label="Open Positions" value={openPositions.length} tone="cyan" /></div>
        <div className={`pl-chart ${chartHasData ? 'has-data' : 'is-empty'}`}>{chartHasData ? <ResponsiveContainer><AreaChart data={chartData} margin={{ top: 14, right: 18, left: 4, bottom: 6 }}><defs><linearGradient id="plGlow" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="#35ff9c" stopOpacity={0.18} /><stop offset="100%" stopColor="#35ff9c" stopOpacity={0.01} /></linearGradient></defs><CartesianGrid stroke="rgba(53,255,156,.08)" vertical={false} /><XAxis dataKey="timestamp" hide /><YAxis domain={chartDomain} tick={{ fill: '#6ee7b7', fontSize: 10 }} width={58} /><Tooltip contentStyle={{ background: '#050807', border: '1px solid rgba(53,255,156,.35)', color: '#d9fff1' }} />{flatEquity ? <><ReferenceLine y={minEquity} stroke="#35ff9c" strokeWidth={2} strokeDasharray={singleEquityPoint ? '4 4' : undefined} />{singleEquityPoint && <ReferenceDot x={(chartData[0] as any).timestamp} y={minEquity} r={4} fill="#35ff9c" stroke="#050807" />}</> : <Area type="monotone" dataKey="equity" stroke="#35ff9c" strokeWidth={3} fill="url(#plGlow)" />}</AreaChart></ResponsiveContainer> : <div className="chart-empty"><span>Waiting for portfolio/equity updates</span></div>}{chartHasData && !equityHistory.length ? <small className="chart-note">Flat equity / waiting for changes</small> : null}</div>
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
        {showDiagnostics && <div className="diagnostics-grid"><MiniBlock title="Scanner" rows={scannerRows} /><MiniBlock title="Edge Distribution" rows={edgeDistributionRows} /><MiniBlock title="Strategy Summary" rows={strategyRows} />{strategyTableRows.length ? <Panel title="Strategy Table" active compact><Table t={strategyTable} /></Panel> : null}<MiniBlock title="Paper Limited" rows={[["Enabled", String(paperDiagnosticsEnabled)], ["Eligible", String(paperDiagnosticsEligibleSafe)], ["Reason", paperDiagnosticsBlockedReason], ["Allowed", runtime(health, 'paperDiagnosticsLimitedAllowedStrategy') ?? '-'], ["Max positions", runtime(health, 'paperDiagnosticsLimitedMaxOpenPositions') ?? '-'], ["Trade cap", money(runtime(health, 'paperDiagnosticsLimitedMaxPaperNotionalPerTrade') ?? 0)], ["Exposure cap", money(runtime(health, 'paperDiagnosticsLimitedMaxPaperTotalExposure') ?? 0)], ["Opens last hour", runtime(health, 'paperDiagnosticsLimitedOpensLastHour') ?? 0], ["Last reject", runtime(health, 'paperDiagnosticsLimitedGateLastRejectReason') ?? 'None'], ["Opened", runtime(health, 'paperDiagnosticsLimitedPaperOpened') ?? 0]]} /><MiniBlock title="Paper Summary" rows={[["Exposure", money(runtime(health, 'paperTotalExposure') ?? paper.totalExposure ?? locked)], ["Settlements", d.paperSettlements?.length ?? paper.settlements ?? 0], ["Rejects", Object.entries(paper.blockedCountsByReason ?? {}).map(([k, v]: any) => `${k}=${v}`).join(' ') || '-']]} /><MiniBlock title="Runtime" rows={[["Source", runtime(health, '__source') ?? d.source], ["Discovery", discoveryLabel(health, scanner, d.controls)], ["Readiness", readinessLabel(health, d.controls)], ["Universe", runtime(health, 'diagnosticsUniverse') ?? '-'], ["Reduced markets", runtime(health, 'reducedUniverseMarkets') ?? 0], ["Updated", time(runtime(health, '__updatedAt') ?? d.lastUpdated)]]} /><MiniBlock title="Orderbook Health" rows={orderbookRows} />{cycleRows.length ? <MiniBlock title="Single Cycle" rows={cycleRows} /> : null}<details className="raw-log-block"><summary>Raw logs</summary><div className="raw-log-console">{d.logs.slice(0, 30).map((l: any) => <pre key={l.id}>{time(l.timestamp)} [{l.source}] {l.message}</pre>)}</div></details></div>}
      </section>
    </main>
  </div>;
}



function strategyCounterMap(health: any) {
  const rawCounters = runtime(health, 'strategyCounters') ?? {};
  const counters: Record<string, any> = {};
  for (const [name, counter] of Object.entries(rawCounters)) counters[name] = normalizeStrategyCounter(counter);
  const structured: Array<[string, string]> = [
    ['strategyScanCounts', 'scan'],
    ['strategyCandidates', 'cand'],
    ['strategyPositiveEdges', 'positive'],
    ['strategyExecutionReady', 'ready'],
    ['strategyPaperOpened', 'paper']
  ];
  for (const [field, target] of structured) {
    const bag = runtime(health, field);
    if (!bag || typeof bag !== 'object') continue;
    for (const [strategy, value] of Object.entries(bag)) {
      counters[strategy] ??= {};
      counters[strategy][target] = Number(value);
    }
  }
  return counters;
}
function normalizeStrategyCounter(counter: any) {
  if (!counter || typeof counter !== 'object') return counter;
  return {
    ...counter,
    scan: first(counter.scan, counter.scanned, counter.Scanned),
    books: first(counter.books, counter.Books),
    cand: first(counter.cand, counter.candidates, counter.Candidates),
    positive: first(counter.positive, counter.positiveEdges, counter.PositiveEdges),
    paper: first(counter.paper, counter.paperOpened, counter.PaperOpened),
    ready: first(counter.ready, counter.executionReady, counter.ExecutionReady),
    shadow: first(counter.shadow, counter.shadowWouldOpen, counter.ShadowWouldOpen),
    bestAfterSafetyEdge: first(counter.bestAfterSafetyEdge, counter.BestAfterSafetyEdge, counter.bestEdge, counter.BestEdge),
    bestValidPricedAfterSafetyEdge: first(counter.bestValidPricedAfterSafetyEdge, counter.BestValidPricedAfterSafetyEdge, counter.bestAfterSafetyEdge, counter.BestAfterSafetyEdge, counter.bestEdge, counter.BestEdge),
    bestExecutableAfterSafetyEdge: first(counter.bestExecutableAfterSafetyEdge, counter.BestExecutableAfterSafetyEdge, (Number(first(counter.executionReady, counter.ExecutionReady, counter.shadowWouldOpen, counter.ShadowWouldOpen, 0)) > 0 ? first(counter.bestAfterSafetyEdge, counter.BestAfterSafetyEdge, counter.bestEdge, counter.BestEdge) : undefined)),
    bestCandidateValid: first(counter.bestCandidateValid, counter.BestCandidateValid, false),
    bestCandidatePriced: first(counter.bestCandidatePriced, counter.BestCandidatePriced, false),
    bestCandidateExecutableLike: first(counter.bestCandidateExecutableLike, counter.BestCandidateExecutableLike, false),
    bestCandidateReason: first(counter.bestCandidateReason, counter.BestCandidateReason, counter.bestRejectedReason, counter.BestRejectedReason),
    validPriced: first(counter.validPriced, counter.ValidPriced, 0),
    invalidOrUnpriced: first(counter.invalidOrUnpriced, counter.InvalidOrUnpriced, 0),
    unverified: first(counter.unverified, counter.Unverified, 0),
    reviewOnly: first(counter.reviewOnly, counter.ReviewOnly, 0),
    missingPricing: first(counter.missingPricing, counter.MissingPricing, 0),
    bestRejectedReason: first(counter.bestRejectedReason, counter.BestRejectedReason, counter.topSkipReason, counter.TopSkipReason)
  };
}
function getStrategyCounter(counters: any, name: string) {
  const key = Object.keys(counters ?? {}).find((k) => k.toLowerCase() === name.toLowerCase());
  return key ? counters[key] : null;
}
function strategyCompact(counter: any, fields: string[]) {
  if (!counter) return '-';
  return fields.map((f) => `${f}=${counter[f] ?? 0}`).join(' / ');
}

function discoveryLabel(health: any, scanner: any, controls: any) {
  const mode = String(first(runtime(health, 'discoveryMode'), runtime(health, 'discoverySelectedSource'), '')).toLowerCase();
  if (runtime(health, 'discoverySourceAuditOnly')) return 'Blocked';
  if (mode === 'reduceduniversediagnosticsonly' || runtime(health, 'discoveryReducedUniverse')) return 'Reduced Universe';
  return first(runtime(health, 'discoverySelectedSource'), scanner?.poolLimitReason, controls?.isPaused ? 'Paused' : undefined, 'Scanning');
}
function readinessLabel(health: any, controls: any) {
  const mode = String(first(runtime(health, 'discoveryMode'), runtime(health, 'discoverySelectedSource'), '')).toLowerCase();
  if (controls?.isPaused) return 'Paused';
  if (runtime(health, 'discoverySourceAuditOnly')) return 'SourceAuditOnly';
  if (mode === 'reduceduniversediagnosticsonly' || runtime(health, 'discoveryReducedUniverse')) return 'Blocked / Reduced diagnostics';
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
