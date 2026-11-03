# Trader.Scout

Code of **Scout**. Project document: [docs/bots/scout.md](../../docs/bots/scout.md); setup rules: Scout's spec ([github.com/ziash/Scout](https://github.com/ziash/Scout), `docs/spec.md`).

`Detector.cs` holds Scout_Pine's rules; `ChartTracker.cs` runs one chart and its higher timeframe; `AccountScout.cs` fetches candles, places and cancels orders and writes the logs on one account; `ScoutState.cs` is what Scout remembers.
