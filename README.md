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
