# TradingBot runtime exports

This directory contains generated runtime diagnostics. Generated files are intentionally ignored by Git and must not be committed.

- `latest/` contains compact current state for operators. Start with `phase1-summary.json`, `operator-runbook.json`, `export-health.json`, and `strategy-comparison.json`.
- `history/` contains bounded JSONL history used for trend analysis.
- `debug/` contains high-volume diagnostics, grouped by diagnostic stream.
- `archive/` contains rotated files and best-effort fallback writes.

When the bot is stopped, it is safe to delete the contents of `debug/` and `archive/` to reclaim disk space. Do not delete paper accounting or execution evidence while positions are open. Do not commit runtime export artifacts.
