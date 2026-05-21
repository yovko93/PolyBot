import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
export type BotEvents = 'botStatusUpdated'|'opportunitiesUpdated'|'opportunityDetected'|'tradeLogUpdated'|'tradeExecuted'|'positionsUpdated'|'riskUpdated'|'scannerStatsUpdated'|'terminalLogAdded'|'heartbeat'|'equityUpdated'|'controlsUpdated';
export class BotSignalR {
  private conn: HubConnection;
  constructor(url: string) { this.conn = new HubConnectionBuilder().withUrl(url).withAutomaticReconnect([0,1000,2000,5000]).configureLogging(LogLevel.Warning).build(); }
  start() { return this.conn.start(); }
  stop() { return this.conn.stop(); }
  on<T>(event: BotEvents, cb: (data: T) => void) { this.conn.on(event, cb); return () => this.conn.off(event, cb); }
  onState(cb: (s: 'CONNECTED'|'RECONNECTING'|'DISCONNECTED') => void) { this.conn.onreconnecting(() => cb('RECONNECTING')); this.conn.onreconnected(() => cb('CONNECTED')); this.conn.onclose(() => cb('DISCONNECTED')); if (this.conn.state === HubConnectionState.Connected) cb('CONNECTED'); }
}
