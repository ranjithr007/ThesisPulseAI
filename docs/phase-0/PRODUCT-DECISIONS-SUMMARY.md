# Phase 0 Product Decisions Summary

## Accepted scope

### Initial market universe

- NIFTY 50;
- BANK NIFTY;
- FINNIFTY;
- versioned allow-list of selected liquid equities.

Universe membership is effective-dated to preserve point-in-time correctness and prevent survivorship bias.

### Trading timeframe hierarchy

- `5m`: primary signal and thesis timeframe;
- `1m`: execution timing and microstructure refinement;
- `15m`: setup and structure confirmation;
- `1h`: market regime and directional context;
- `1d`: structural trend, volatility, gaps, and major levels.

Closed candles are authoritative by default. One-minute observations cannot increase approved quantity, widen risk, or turn a rejected five-minute setup into an executable trade.

### Instrument rollout

1. Cash-equity data, intelligence, paper, and shadow.
2. Cash-equity restricted live.
3. Index-futures data, intelligence, paper, and shadow.
4. Index-futures restricted live.
5. Options chain intelligence, research, backtesting, and paper trading.
6. Options shadow validation.
7. Restricted-live single-leg options only after dedicated acceptance criteria.
8. Multi-leg live options deferred until a separate ADR.

### Initial restrictions

- intraday only;
- overnight exposure disabled;
- no averaging down;
- no martingale sizing;
- no automatic intraday-to-delivery conversion;
- no raw broker contract token supplied directly by a model;
- no live multi-leg options;
- live universe narrower than paper and shadow universes.

## Related decisions

- `ADR-0002-initial-market-and-instrument-universe.md`
- `ADR-0003-trading-timeframes-and-confirmation-hierarchy.md`
- `ADR-0004-cash-futures-and-options-rollout-scope.md`
