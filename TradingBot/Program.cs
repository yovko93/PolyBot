using TradingBot.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddPolicy("ui", p => p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSignalR();
builder.Services.AddSingleton<BotRuntimeState>();
builder.Services.AddSingleton<BotEventPublisher>();
builder.Services.AddHostedService<BotHostedService>();

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

app.Run("http://localhost:5000");
