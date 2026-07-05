# PolyBot
## UI/Backend live data
Backend API/Hub default: http://localhost:5000

## Local verification (.NET SDK)
Run all backend validation commands from the repository root (where `TradingBot.sln` is located):

```powershell
dotnet --info
dotnet restore
dotnet build
dotnet test
```

Notes:
- The solution now includes both backend and test projects:
  - `TradingBot/TradingBot.csproj`
  - `TradingBot.Tests/TradingBot.Tests.csproj`
- Cross-exchange/Kalshi defaults are conservative and paper-first (`EnableLiveExecution=false`, `CrossExchangeArbitrage:Enabled=false`, `PaperOnly=true`).

## Runtime profiles

### Reduced diagnostics full stack run

Use the safe diagnostics preset instead of passing the full feature-flag command line:

```bash
dotnet run --project TradingBot -- --profile ReducedDiagnosticsFullStack
```

Equivalent config-key form:

```bash
dotnet run --project TradingBot -- --TradingBot:RuntimeProfile=ReducedDiagnosticsFullStack
```

Optional override example:

```bash
dotnet run --project TradingBot -- --profile ReducedDiagnosticsFullStack --TradingBot:FocusUniverse:MaxWatchlistItems=100
```

The profile keeps live trading disabled, uses `SingleMarketBuyBoth` as the only paper-eligible strategy, keeps verified/auto-candidate multi-outcome strategies shadow-only, and keeps focus/edge/compression/spread layers diagnostics-only.
