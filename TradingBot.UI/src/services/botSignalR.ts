import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
export type BotEvents = 'botStatusUpdated'|'opportunitiesUpdated'|'opportunityDetected'|'tradeLogUpdated'|'tradeExecuted'|'positionsUpdated'|'riskUpdated'|'scannerStatsUpdated'|'terminalLogAdded'|'heartbeat'|'equityUpdated'|'controlsUpdated'|'singleMarketArbsUpdated'|'singleMarketPaperExecutionsUpdated';
export class BotSignalR {
  private static activeInstance: BotSignalR | null = null;
  private conn: HubConnection;
  constructor(url: string) { this.conn = new HubConnectionBuilder().withUrl(url).withAutomaticReconnect([0,1000,2000,5000]).configureLogging(LogLevel.Warning).build(); }
  static getOrCreate(url: string) { if (!BotSignalR.activeInstance) BotSignalR.activeInstance = new BotSignalR(url); return BotSignalR.activeInstance; }
  start() { return this.conn.start(); }
  stop() { BotSignalR.activeInstance = null; return this.conn.stop(); }
  on<T>(event: BotEvents, cb: (data: T) => void) { this.conn.on(event, cb); return () => this.conn.off(event, cb); }
  onState(cb: (s: 'CONNECTED'|'RECONNECTING'|'DISCONNECTED') => void) { this.conn.onreconnecting(() => cb('RECONNECTING')); this.conn.onreconnected(() => cb('CONNECTED')); this.conn.onclose(() => cb('DISCONNECTED')); if (this.conn.state === HubConnectionState.Connected) cb('CONNECTED'); }
}
