import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
export type BotEvents = 'botStatusUpdated'|'opportunitiesUpdated'|'opportunityDetected'|'tradeLogUpdated'|'tradeExecuted'|'positionsUpdated'|'riskUpdated'|'scannerStatsUpdated'|'terminalLogAdded'|'heartbeat'|'equityUpdated'|'controlsUpdated'|'singleMarketArbsUpdated'|'singleMarketPaperExecutionsUpdated';
export class BotSignalR {
  private static activeInstance: BotSignalR | null = null;
  private conn: HubConnection;
  private startPromise: Promise<void> | null = null;
  constructor(url: string) { this.conn = new HubConnectionBuilder().withUrl(url).withAutomaticReconnect([0,1000,2000,5000]).configureLogging(LogLevel.Warning).build(); }
  static getOrCreate(url: string) { if (!BotSignalR.activeInstance) BotSignalR.activeInstance = new BotSignalR(url); return BotSignalR.activeInstance; }
  start() {
    if (this.conn.state === HubConnectionState.Connected) return Promise.resolve();
    if (this.conn.state === HubConnectionState.Connecting || this.conn.state === HubConnectionState.Reconnecting) return this.startPromise ?? Promise.resolve();
    this.startPromise = this.conn.start().finally(() => { this.startPromise = null; });
    return this.startPromise;
  }
  stop() { BotSignalR.activeInstance = null; this.startPromise = null; return this.conn.stop(); }
  on<T>(event: BotEvents, cb: (data: T) => void) { this.conn.on(event, cb); return () => this.conn.off(event, cb); }
  onState(cb: (s: 'CONNECTED'|'RECONNECTING'|'DISCONNECTED') => void) {
    let active = true;
    const emit = (state: 'CONNECTED'|'RECONNECTING'|'DISCONNECTED') => { if (active) cb(state); };
    this.conn.onreconnecting(() => emit('RECONNECTING'));
    this.conn.onreconnected(() => emit('CONNECTED'));
    this.conn.onclose(() => emit('DISCONNECTED'));
    if (this.conn.state === HubConnectionState.Connected) emit('CONNECTED');
    return () => { active = false; };
  }
}
