using System.Text;
using Microsoft.AspNetCore.SignalR;
using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Services;

var originalOut = Console.Out;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddPolicy("ui", p => p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSignalR();
builder.Services.AddSingleton<BotRuntimeState>();
builder.Services.AddSingleton<TextWriter>(originalOut);
builder.Services.AddSingleton<IBotUiLogger, BotUiLogger>();

var app = builder.Build();
app.UseCors("ui");
const string listenUrl = "http://localhost:5000";

app.MapGet("/api/bot/health", () => Results.Ok(new { ok = true, service = "PolyBot", timestamp = DateTime.UtcNow }));
app.MapGet("/api/bot/status", (BotRuntimeState s) => s.Status);
app.MapGet("/api/bot/opportunities", (BotRuntimeState s) => s.Opportunities());
app.MapGet("/api/bot/positions", (BotRuntimeState s) => s.Positions());
app.MapGet("/api/bot/trade-log", (BotRuntimeState s) => s.Trades());
app.MapGet("/api/bot/scanner-stats", (BotRuntimeState s) => s.ScannerStats);
app.MapGet("/api/bot/risk", (BotRuntimeState s) => s.Risk);
app.MapGet("/api/bot/logs/recent", (BotRuntimeState s) => s.Logs());
app.MapGet("/api/bot/equity", (BotRuntimeState s) => s.Equity());
app.MapPost("/api/bot/dev/test-event", async (BotRuntimeState state, IHubContext<BotHub> hub, IBotUiLogger logger) =>
{
    logger.LogInfo("dev", "[DEV TEST] UI pipeline test event");
    var opp = new OpportunityDto(Guid.NewGuid().ToString("N"), DateTime.UtcNow, 1, "DEV_TEST", "DEV", "Dev Market", "BOTH", 0.012m, 1.25m, 98m, 100m, 10m, true, "EXECUTABLE", null, state.NextSeq());
    state.AddOpportunity(opp);
    var stats = new ScannerStatsDto(25, 40, 3, 2, 1, 1200, DateTime.UtcNow.AddSeconds(-2), DateTime.UtcNow, state.NextSeq());
    state.SetScannerStats(stats);
    state.AddEquity(new EquityPointDto(DateTime.UtcNow, state.Status.Equity + 1.23m, state.NextSeq()));
    var status = state.Status with { ConnectionStatus = "CONNECTED", ScannerActive = true, SignalCount = 1, LastHeartbeat = DateTime.UtcNow, LastScanTime = DateTime.UtcNow, Equity = state.Status.Equity + 1.23m };
    state.SetStatus(status);
    await hub.Clients.All.SendAsync("opportunityDetected", opp);
    await hub.Clients.All.SendAsync("opportunitiesUpdated", state.Opportunities());
    await hub.Clients.All.SendAsync("scannerStatsUpdated", state.ScannerStats);
    await hub.Clients.All.SendAsync("botStatusUpdated", state.Status);
    await hub.Clients.All.SendAsync("equityUpdated", state.Equity());
    return Results.Ok(new { ok = true });
});
app.MapHub<BotHub>("/hubs/bot");

var apiTask = app.RunAsync(listenUrl);
var state = app.Services.GetRequiredService<BotRuntimeState>();
var logger = app.Services.GetRequiredService<IBotUiLogger>();
Console.SetOut(new MultiTextWriter(originalOut, msg => logger.LogInfo("console", msg)));
logger.LogSuccess("startup", $"Bot API listening on {listenUrl}");
logger.LogSuccess("startup", "SignalR hub available at /hubs/bot");

_ = Task.Run(async () =>
{
    var hub = app.Services.GetRequiredService<IHubContext<BotHub>>();
    while (true)
    {
        state.SetStatus(state.Status with { LastHeartbeat = DateTime.UtcNow, ConnectionStatus = "CONNECTED" });
        await hub.Clients.All.SendAsync("heartbeat", new { timestamp = DateTime.UtcNow, sequence = state.NextSeq() });
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
});

await RunScannerAsync(state, logger, app.Services.GetRequiredService<IHubContext<BotHub>>());
await apiTask;

static async Task RunScannerAsync(BotRuntimeState state, IBotUiLogger uiLogger, IHubContext<BotHub> hub)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    http.Timeout = TimeSpan.FromSeconds(10);
    var marketService = new MarketDataService(http);
    var orderbookService = new OrderBookService(http) { DisableSingleBookHttpFallback = true };
    var executionPolicy = new ExecutionPolicy { MaxNotionalPerTrade = 100m, MinNotionalPerTrade = 5m, MinEdgePerShare = 0.003m, MinExpectedProfit = 0.25m, MaxLockedCapital = 300m, MaxOpenPositions = 5, MaxExposurePerGroup = 100m, AllowBasketArbs = true, AllowSingleMarketArbs = true, AllowCompleteSetSellArbs = true, AllowThresholdArbs = true };
    var sizing = new ExecutionSizingService(executionPolicy);
    var executionDecisionService = new ExecutionDecisionService(executionPolicy);
    var executionJournalPath = Path.Combine(AppContext.BaseDirectory, "data", "execution-journal.csv");
    var executionJournal = new ExecutionJournal(executionJournalPath);
    var positionBook = new PaperPositionBook(Path.Combine(AppContext.BaseDirectory, "data", "paper-positions.csv"));
    var paper = new PaperTradingEngine(executionPolicy, executionJournal, executionDecisionService, positionBook);
    var monitor = new OpportunityMonitor(Path.Combine(AppContext.BaseDirectory, "data", "arb-opportunities.csv"),0.003m,-0.02m,TimeSpan.FromMinutes(2),0.25m,new DryRunLiveOrderBuilder(minEdgePerShare: -0.01m, maxPlanCost: 100000m, minSize: 1m, tickSize: 0.001m, orderType: LiveOrderType.FOK, policy: executionPolicy));
    var semaphore = new SemaphoreSlim(5);
    var singleMarketArb = new SingleMarketOrderBookArbEngine(orderbookService,0.003m,0.001m,0.001m,monitor,sizing);

    List<Market> cachedMarkets = new();
    while (true)
    {
        var started = DateTime.UtcNow;
        try
        {
            uiLogger.LogInfo("scanner", "[SCAN] cycle start");
            monitor.BeginCycle();
            cachedMarkets = await marketService.GetMarketsAsync();
            var filtered = cachedMarkets.Where(m => m?.outcomes?.Count == 2 && m.clobTokenIds?.Count >= 2).Take(200).ToList();
            await orderbookService.PrefetchBinarySnapshotsAsync(filtered);
            await singleMarketArb.ScanAsync(filtered!, paper, semaphore);
            monitor.PrintCycleRanking(top: 10, executableOnly: false);
            monitor.FlushCsv();
            SyncRuntimeState(state, monitor, positionBook, executionJournalPath, executionPolicy, orderbookService, paper, filtered.Count, started);
            await hub.Clients.All.SendAsync("opportunitiesUpdated", state.Opportunities());
            await hub.Clients.All.SendAsync("tradeLogUpdated", state.Trades());
            await hub.Clients.All.SendAsync("positionsUpdated", state.Positions());
            await hub.Clients.All.SendAsync("scannerStatsUpdated", state.ScannerStats);
            await hub.Clients.All.SendAsync("riskUpdated", state.Risk);
            await hub.Clients.All.SendAsync("botStatusUpdated", state.Status);
            await hub.Clients.All.SendAsync("equityUpdated", state.Equity());
            uiLogger.LogSuccess("scanner", "[SCAN] cycle completed");
        }
        catch (Exception ex) { uiLogger.LogError("scanner", $"[PROGRAM ERROR] {ex.Message}"); }
        await Task.Delay(3000);
    }
}

static void SyncRuntimeState(BotRuntimeState state, OpportunityMonitor monitor, PaperPositionBook pb, string executionJournalPath, ExecutionPolicy p, OrderBookService obs, PaperTradingEngine paper, int marketsScanned, DateTime scanStart)
{
    var top = monitor.GetTopCycleRecords(200, executableOnly:false);
    foreach (var (r,i) in top.Select((x,i)=>(x,i))) state.AddOpportunity(new OpportunityDto($"{r.Engine}-{r.Key}-{i}",r.TimestampUtc,i+1,r.Strategy,r.GroupKey??"",r.Leg1,"BOTH",r.EdgePerShare,r.ExpectedProfit,r.CostOrProceeds,r.GuaranteedPayout,r.QuantityAvailable,r.IsExecutable,r.IsExecutable?"EXECUTABLE":"DETECTED",null,state.NextSeq()));
    foreach (var pz in pb.OpenPositions.Concat(pb.ClosedPositions).Take(200)) state.AddPosition(new PaperPositionDto(pz.PositionId,pz.OpenedAtUtc,pz.ClosedAtUtc,pz.Strategy,pz.GroupKey,pz.Legs.Select(l=>$"{l.Outcome}:{l.Question}").ToList(),pz.TotalCost,pz.GuaranteedPayout,pz.ExpectedProfit,pz.RealizedPayout,pz.RealizedProfit,pz.Status.ToString().ToUpperInvariant(),state.NextSeq()));
    if (File.Exists(executionJournalPath)) foreach (var line in File.ReadLines(executionJournalPath).Skip(1).TakeLast(100)) { var c=line.Split(','); if(c.Length<10) continue; state.AddTrade(new TradeLogEntryDto(Guid.NewGuid().ToString("N"),DateTime.UtcNow,"SCAN","BASKET",c[4],0,0,0,0,"SKIPPED",null,state.NextSeq())); }
    var s = obs.GetStats();
    state.SetScannerStats(new ScannerStatsDto(marketsScanned,(int)Math.Min(int.MaxValue, s.BatchBooksLoaded),top.Count,top.Count(x=>x.IsExecutable),Math.Max(0,top.Count-top.Count(x=>x.IsExecutable)),(long)(DateTime.UtcNow-scanStart).TotalMilliseconds,scanStart,DateTime.UtcNow,state.NextSeq()));
    state.SetRisk(new RiskStateDto(p.MaxNotionalPerTrade,p.MinNotionalPerTrade,p.MinEdgePerShare,p.MinExpectedProfit,p.MaxLockedCapital,paper.LockedCapital,p.MaxOpenPositions,pb.OpenPositions.Count,p.MaxExposurePerGroup,new Dictionary<string,decimal>(),p.AllowBasketArbs,p.AllowSingleMarketArbs,p.AllowCompleteSetSellArbs,p.AllowThresholdArbs,DateTime.UtcNow,state.NextSeq()));
    state.SetStatus(new BotStatusDto("PAPER",true,"CONNECTED",paper.Balance,paper.LockedCapital,paper.Equity,0m,paper.ExpectedProfit,pb.OpenPositions.Count,top.Count,DateTime.UtcNow,DateTime.UtcNow));
    state.AddEquity(new EquityPointDto(DateTime.UtcNow, paper.Equity, state.NextSeq()));
}

public class MultiTextWriter(TextWriter original, Action<string> mirror) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;
    public override void WriteLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        original.WriteLine(value);
        mirror(value);
    }
}
