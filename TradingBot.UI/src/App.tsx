import { useMemo, useState } from 'react';
import { useBotData } from './hooks/useBotData';

export default function App(){
  const {status,opps,trades,positions,scanner,risk,logs,connectionStatus,lastHeartbeat,lastUpdated,isMock}=useBotData();
  const [search,setSearch]=useState(''); const [minEdge,setMinEdge]=useState(0); const [execOnly,setExecOnly]=useState(false);
  const filtered = useMemo(()=>opps.filter(o=>(!execOnly||o.executable)&&o.edgePerShare>=minEdge&&(`${o.group} ${o.market}`.toLowerCase().includes(search.toLowerCase()))).sort((a,b)=>b.edgePerShare-a.edgePerShare),[opps,execOnly,minEdge,search]);
  const mode=status?.mode ?? 'UNKNOWN';
  return <div className='terminal-root p-3 text-green-100'>
    <div className='terminal-panel p-2 mb-2'>BACKEND: {connectionStatus} | MODE: {mode} | Last heartbeat: {lastHeartbeat || '-'} | Last update: {lastUpdated || '-'} | SOURCE: {isMock?'MOCK FALLBACK':'LIVE BACKEND'}</div>
    {mode==='LIVE' && <div className='terminal-panel p-2 mb-2 text-rose-400'>WARNING: LIVE MODE</div>}
    <div className='grid grid-cols-3 gap-2'>
      <section className='terminal-panel p-2'><h3>Opportunity Ranking</h3><input placeholder='search group/market' value={search} onChange={e=>setSearch(e.target.value)} /><input type='number' value={minEdge} onChange={e=>setMinEdge(Number(e.target.value)||0)} /><label><input type='checkbox' checked={execOnly} onChange={e=>setExecOnly(e.target.checked)} /> executable only</label>{filtered.slice(0,100).map(o=><div key={o.id}>{o.strategy} | {o.market} | {o.edgePerShare}</div>)}</section>
      <section className='terminal-panel p-2'><h3>Trade Log</h3>{trades.slice(0,100).map(t=><div key={t.id}>{t.timestamp} {t.status} {t.market}</div>)}</section>
      <section className='terminal-panel p-2'><h3>Positions</h3>{positions.slice(0,100).map(p=><div key={p.id}>{p.group} {p.status} {p.expectedProfit}</div>)}</section>
    </div>
    <div className='grid grid-cols-3 gap-2 mt-2'>
      <section className='terminal-panel p-2'><h3>Risk</h3><div>Max Locked {risk?.maxLockedCapital}</div></section>
      <section className='terminal-panel p-2'><h3>Scanner</h3><div>Markets {scanner?.marketsScanned}</div></section>
      <section className='terminal-panel p-2'><h3>Terminal</h3>{logs.slice(0,30).map(l=><div key={l.id}>{l.level} {l.message}</div>)}</section>
    </div>
  </div>
}
