# TradingBot.UI

Professional desktop-style dashboard UI for PolyBot (UI-only; no real order placement).

## Run locally
1. `npm install`
2. `npm run dev`
3. Open the printed Vite URL.

## Mock data location
- `src/data/mockData.ts`
- Accessed via service facade in `src/services/botApi.ts`.

## Backend integration plan (.NET)
Replace mocked implementations in `src/services/botApi.ts` with:
- REST polling for `getBotStatus`, `getOpportunities`, `getPositions`, `getTradeLogs`, `getScannerStats`
- SignalR/WebSocket stream inside `subscribeToBotEvents()`

The dashboard components already consume typed interfaces from `src/types/models.ts`, so backend wiring can be changed without UI rewrites.
