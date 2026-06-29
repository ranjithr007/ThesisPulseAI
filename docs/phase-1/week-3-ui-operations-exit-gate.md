# Phase 1 Week 3 — UI and Operations Exit Gate

## Delivered routes

The React shell uses dependency-free hash routing so deep links work in local development and static hosting without server rewrite rules.

```text
#/signals
#/signals/{signalUid}
#/operations
```

## Signal scanner exit criteria

- Loads the canonical signal snapshot from Signal Service.
- Subscribes to Trading API SignalR updates.
- Recovers recent stream events after refresh.
- Falls back to REST polling when SignalR is unavailable.
- Filters by instrument or strategy, direction, status, and timeframe.
- Shows strength, confidence, lifecycle state, and validity.
- Links every signal to a refresh-safe detail URL.
- Keeps the PAPER environment warning permanently visible.

## Signal detail exit criteria

- Loads the selected signal by UID from Signal Service.
- Supports direct browser navigation and page refresh.
- Shows direction, timeframe, strength, confidence, lifecycle, freshness, strategy identity, engine identity, producer, and immutable IDs.
- Provides an explicit return path to the scanner.
- States that risk and order handling remain outside the UI page.

## Operations dashboard exit criteria

The dashboard is read-only and polls every ten seconds.

It shows:

- automatic signal-expiry scheduler enabled state;
- interval and batch size;
- latest run state and timestamps;
- selected, expired, published, and publication-failure counts;
- run correlation ID and last error;
- Signal Service readiness;
- Trading API readiness;
- request duration and HTTP result for each dependency.

The browser cannot start a job, edit a schedule, change a signal status, or modify trading state.

## Operations API

```text
GET /api/v1/jobs/signal-expiry
GET /api/v1/platform/health
```

`/api/v1/platform/health` returns:

- `200` for `HEALTHY` or `DEGRADED`;
- `503` when all configured downstream dependencies are unavailable.

The response body is always returned so the dashboard can show dependency-level diagnostics.

## Local URLs

```text
Trading API:      http://localhost:5100
Signal Service:   http://localhost:5102
Operations:       http://localhost:5107
React:            http://localhost:5173
```

Frontend environment variables:

```text
VITE_SIGNAL_API_BASE_URL=http://localhost:5102
VITE_TRADING_API_BASE_URL=http://localhost:5100
VITE_OPERATIONS_API_BASE_URL=http://localhost:5107
```

## Verification flow

1. Start Trading API, Signal Service, Operations Service, and React.
2. Open `#/signals` and verify the scanner snapshot loads.
3. Open a signal and confirm `#/signals/{signalUid}` survives refresh.
4. Stop Trading API and verify the scanner enters REST fallback.
5. Open `#/operations` and verify Trading API reports unhealthy while Signal Service remains visible independently.
6. Restart Trading API and verify health returns without refreshing the browser manually.
7. Confirm disabled navigation entries cannot be activated.
8. Confirm no UI endpoint creates an order, risk decision, or broker request.

## Exit gate

Week 3 is complete when:

- .NET build and tests pass;
- Python lint and tests pass;
- React strict type-check and production build pass;
- scanner, detail, and operations routes render;
- service readiness and scheduler state are observable;
- all write actions remain server-controlled and PAPER-only.
