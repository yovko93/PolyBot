using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddPolicy("ui", p => p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSignalR();
builder.Services.AddSingleton<BotRuntimeState>();
builder.Services.AddSingleton<IBotUiLogger, BotUiLogger>();

var app = builder.Build();
app.UseCors("ui");

app.MapGet("/api/bot/health", () => Results.Ok(new { ok = true, timestamp = DateTime.UtcNow }));
app.MapGet("/api/bot/status", (BotRuntimeState s) => s.Status);
app.MapGet("/api/bot/opportunities", (BotRuntimeState s) => s.Opportunities());
app.MapGet("/api/bot/positions", (BotRuntimeState s) => s.Positions());
app.MapGet("/api/bot/trade-log", (BotRuntimeState s) => s.Trades());
app.MapGet("/api/bot/scanner-stats", (BotRuntimeState s) => s.ScannerStats);
app.MapGet("/api/bot/risk", (BotRuntimeState s) => s.Risk);
app.MapGet("/api/bot/logs/recent", (BotRuntimeState s) => s.Logs());
app.MapHub<BotHub>("/hubs/bot");

var apiTask = app.RunAsync("http://localhost:5000");

var state = app.Services.GetRequiredService<BotRuntimeState>();
var logger = app.Services.GetRequiredService<IBotUiLogger>();
var originalOut = Console.Out;
Console.SetOut(new MultiTextWriter(originalOut, new UiConsoleTextWriter(logger)));

_ = Task.Run(async ()=>{ while(true){ var hb = new { timestamp = DateTime.UtcNow, sequence = state.NextSeq()}; await app.Services.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<BotHub>>().Clients.All.SendAsync("heartbeat", hb); await Task.Delay(3000);} });

await RunScannerAsync(state, logger, app.Services.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<BotHub>>());
await apiTask;

static async Task RunScannerAsync(BotRuntimeState state, IBotUiLogger uiLogger, Microsoft.AspNetCore.SignalR.IHubContext<BotHub> hub)
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
    var settlementService = new PaperSettlementService(paper, positionBook);
    var dryRunOrderBuilder = new DryRunLiveOrderBuilder(-0.01m,100000m,1m,0.001m,LiveOrderType.FOK,executionPolicy);
    var monitor = new OpportunityMonitor(Path.Combine(AppContext.BaseDirectory, "data", "arb-opportunities.csv"),0.003m,-0.02m,TimeSpan.FromMinutes(2),0.25m,dryRunOrderBuilder);
    var semaphore = new SemaphoreSlim(5);
    var depthSimulator = new OrderBookDepthSimulator(orderbookService);
    var requoteGate = new RequoteExecutionGate(depthSimulator);
    var singleMarketArb = new SingleMarketOrderBookArbEngine(orderbookService,0.003m,0.001m,0.001m,monitor,sizing);
    var completeSetSellArb = new CompleteSetSellArbEngine(orderbookService,0.003m,0.001m,0.001m,monitor,sizing);
    var thresholdArb = new ThresholdDominanceArbEngine(orderbookService,0.005m,0.001m,0.001m,monitor,sizing);
    var multiOutcomeArb = new MultiOutcomeGroupArbEngine(orderbookService,0.005m,0.001m,0.001m,false,monitor,requoteGate,executionDecisionService);
    var semanticMatcher = new SemanticMarketMatcher(0.93,false);
    var trueArb = new TrueArbitrageEngine(orderbookService,semanticMatcher,0.01m,0.002m,0.002m,monitor,sizing);
    var simResolution = new SimulatedResolutionEngine(TimeSpan.FromMinutes(2));
    var nextMarketRefreshUtc = DateTime.MinValue; var nextTrueArbScanUtc = DateTime.MinValue; List<Market> cachedMarkets = new();

    while (true)
    {
        var started = DateTime.UtcNow;
        try
        {
            monitor.BeginCycle();
            if (DateTime.UtcNow >= nextMarketRefreshUtc){cachedMarkets = await marketService.GetMarketsAsync(); nextMarketRefreshUtc = DateTime.UtcNow.AddMinutes(1);}            
            var filtered = cachedMarkets.Where(m => m != null && !string.IsNullOrWhiteSpace(m.question) && m.outcomes != null && m.outcomes.Count == 2 && m.clobTokenIds != null && m.clobTokenIds.Count >= 2).Take(1000).ToList();
            await orderbookService.PrefetchBinarySnapshotsAsync(filtered);
            await singleMarketArb.ScanAsync(filtered, paper, semaphore);
            await completeSetSellArb.ScanAsync(filtered, paper, semaphore);
            await thresholdArb.ScanAsync(filtered, paper, semaphore);
            await multiOutcomeArb.ScanAsync(filtered, paper, semaphore);
            if (DateTime.UtcNow >= nextTrueArbScanUtc){ await trueArb.ScanAsync(filtered, paper, semaphore); nextTrueArbScanUtc = DateTime.UtcNow.AddMinutes(1);}            
            simResolution.Scan(paper);
            monitor.PrintCycleRanking(top: 15, executableOnly: false);
            monitor.FlushCsv();
            SyncRuntimeState(state, monitor, positionBook, executionJournalPath, executionPolicy, orderbookService, paper, filtered.Count, started);
            await hub.Clients.All.SendAsync("opportunitiesUpdated", state.Opportunities());
            await hub.Clients.All.SendAsync("positionsUpdated", state.Positions());
            await hub.Clients.All.SendAsync("tradeLogUpdated", state.Trades());
            await hub.Clients.All.SendAsync("scannerStatsUpdated", state.ScannerStats);
            await hub.Clients.All.SendAsync("riskUpdated", state.Risk);
            await hub.Clients.All.SendAsync("botStatusUpdated", state.Status);
            settlementService.PrintSettlementCandidates();
            orderbookService.ClearExpiredCache();
        }
        catch (Exception ex){ uiLogger.Error("program", $"[PROGRAM ERROR] {ex.Message}"); }
        await Task.Delay(3000);
    }
}

static void SyncRuntimeState(BotRuntimeState state, OpportunityMonitor monitor, PaperPositionBook pb, string executionJournalPath, ExecutionPolicy p, OrderBookService obs, PaperTradingEngine paper, int marketsScanned, DateTime scanStart)
{
    var top = monitor.GetTopCycleRecords(200, executableOnly:false);
    foreach (var (r,i) in top.Select((x,i)=>(x,i))) state.AddOpportunity(new OpportunityDto($"{r.Engine}-{r.Key}-{i}",r.TimestampUtc,i+1,r.Strategy,r.GroupKey??"",r.Leg1,r.Leg1.Contains(" NO ")?"NO":"YES",r.EdgePerShare,r.ExpectedProfit,r.CostOrProceeds,r.GuaranteedPayout,r.QuantityAvailable,r.IsExecutable,r.IsExecutable?"EXECUTABLE":"DETECTED",null,state.NextSeq()));
    foreach (var pz in pb.OpenPositions.Concat(pb.ClosedPositions).Take(200)) state.AddPosition(new PaperPositionDto(pz.PositionId,pz.OpenedAtUtc,pz.ClosedAtUtc,pz.Strategy,pz.GroupKey,pz.Legs.Select(l=>$"{l.Outcome}:{l.Question}").ToList(),pz.TotalCost,pz.GuaranteedPayout,pz.ExpectedProfit,pz.RealizedPayout,pz.RealizedProfit,pz.Status.ToString().ToUpperInvariant(),state.NextSeq()));
    if (File.Exists(executionJournalPath)) foreach (var line in File.ReadLines(executionJournalPath).Skip(1).TakeLast(500)) { var c=SplitCsv(line); if(c.Length<14) continue; state.AddTrade(new TradeLogEntryDto(Guid.NewGuid().ToString("N"),DateTime.TryParse(c[0],out var t)?t:DateTime.UtcNow,c[3],"BASKET",c[4],Parse(c[5]),0,Parse(c[8]),Parse(c[9]),c[13].Contains("EXECUTED")?"PAPER_EXECUTED":"SKIPPED",null,state.NextSeq())); }
    var s = obs.GetStats();
    state.SetScannerStats(new ScannerStatsDto(marketsScanned,s.BatchBooksLoaded,top.Count,top.Count(x=>x.Executable),Math.Max(0,top.Count-top.Count(x=>x.Executable)),(long)(DateTime.UtcNow-scanStart).TotalMilliseconds,scanStart,DateTime.UtcNow,state.NextSeq()));
    state.SetRisk(new RiskStateDto(p.MaxNotionalPerTrade,p.MinNotionalPerTrade,p.MinEdgePerShare,p.MinExpectedProfit,p.MaxLockedCapital,paper.LockedCapital,p.MaxOpenPositions,pb.OpenPositions.Count,p.MaxExposurePerGroup,new Dictionary<string,decimal>(),p.AllowBasketArbs,p.AllowSingleMarketArbs,p.AllowCompleteSetSellArbs,p.AllowThresholdArbs,DateTime.UtcNow,state.NextSeq()));
    state.SetStatus(new BotStatusDto("PAPER",true,"CONNECTED",paper.Balance,paper.LockedCapital,paper.Equity,paper.RealizedProfit,paper.ExpectedProfit,pb.OpenPositions.Count,top.Count,DateTime.UtcNow,DateTime.UtcNow));
}
static decimal Parse(string s)=>decimal.TryParse(s.Trim('"'),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out var d)?d:0m;
static string[] SplitCsv(string line)=>Microsoft.VisualBasic.FileIO.TextFieldParserExtensions.Invoke(line);

public class MultiTextWriter(params TextWriter[] writers) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;
    public override void WriteLine(string? value){ foreach (var w in writers) w.WriteLine(value); }
}

namespace Microsoft.VisualBasic.FileIO { public static class TextFieldParserExtensions { public static string[] Invoke(string line){var result=new List<string>();bool q=false;var cur=""; foreach(var ch in line){ if(ch=='"') q=!q; else if(ch==',' && !q){ result.Add(cur); cur="";} else cur+=ch;} result.Add(cur); return result.Select(x=>x.Trim('"')).ToArray(); } } }
