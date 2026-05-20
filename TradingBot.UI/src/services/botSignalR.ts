import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';

export type BotEvents = 'botStatusUpdated'|'opportunitiesUpdated'|'opportunityDetected'|'tradeLogUpdated'|'tradeExecuted'|'positionsUpdated'|'riskUpdated'|'scannerStatsUpdated'|'terminalLogAdded'|'heartbeat';

export class BotSignalR {
  private conn: HubConnection;
  constructor(url: string){ this.conn = new HubConnectionBuilder().withUrl(url).withAutomaticReconnect().configureLogging(LogLevel.Warning).build(); }
  start(){ return this.conn.start(); }
  stop(){ return this.conn.stop(); }
  on<T>(event: BotEvents, cb: (data:T)=>void){ this.conn.on(event, cb); return () => this.conn.off(event, cb); }
  onClose(cb:()=>void){ this.conn.onclose(cb); }
  onReconnecting(cb:()=>void){ this.conn.onreconnecting(cb); }
  onReconnected(cb:()=>void){ this.conn.onreconnected(cb); }
  status(){ const s = this.conn.state; if (s===HubConnectionState.Connected) return 'CONNECTED'; if (s===HubConnectionState.Reconnecting) return 'RECONNECTING'; return 'DISCONNECTED'; }
}
