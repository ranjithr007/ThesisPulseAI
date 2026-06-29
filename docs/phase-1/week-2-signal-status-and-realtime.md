# Week 2 — Signal Status and Real-Time Stream

## Signal lifecycle

Signal Service now appends lifecycle events to `intelligence.signal_status_events`.

Allowed transitions:

| Current | Allowed next status |
|---|---|
| `CANDIDATE` | `VALIDATED`, `REJECTED`, `EXPIRED`, `SUPERSEDED` |
| `VALIDATED` | `EXPIRED`, `SUPERSEDED`, `CONSUMED` |
| `REJECTED` | Terminal |
| `EXPIRED` | Terminal |
| `SUPERSEDED` | Terminal |
| `CONSUMED` | Terminal |

Every transition requires a unique `transitionUid`, source service/version, correlation ID and UTC event time. Rejection requires at least one reason code. Supersession requires a different existing signal ID.

Endpoint:

```text
POST /api/v1/signals/{signalUid}/status
```

Example:

```json
{
  "transitionUid": "5dc796f3-e74b-4d4d-93d0-3033af0bd6af",
  "targetStatus": "VALIDATED",
  "reasonCodes": ["THESIS_CONFIRMED"],
  "occurredAtUtc": "2026-06-29T16:30:00Z",
  "sourceService": "ThesisPulse.Thesis.Service",
  "sourceVersion": "0.1.0",
  "correlationId": "598de88b-bc72-4d4f-a0bd-4d3c7272b665",
  "causationId": null,
  "relatedSignalUid": null,
  "metadata": {
    "environment": "PAPER"
  }
}
```

Repeated transition IDs are acknowledged without creating a second event.

## Real-time delivery

Trading API hosts a SignalR hub:

```text
/hubs/signals
```

Clients listen for:

```text
signalUpdated
```

Signal Service publishes a versioned `signal.summary.changed.v1` event after a signal is accepted or a lifecycle transition is committed.

Real-time delivery is fail-soft. Signal persistence is authoritative. A publication failure is returned in the API response but does not roll back the signal or status event.

## Secure local configuration

Real-time ingestion and publication are disabled by default.

Set the same internal API key through user secrets for both services:

```powershell
dotnet user-secrets set `
  "SignalStream:InternalApiKey" `
  "<generated-local-secret>" `
  --project src/ThesisPulse.Trading.Api

dotnet user-secrets set `
  "SignalRealtime:InternalApiKey" `
  "<generated-local-secret>" `
  --project src/ThesisPulse.Signal.Service
```

Enable Trading API ingestion:

```text
SignalStream:IngestionEnabled=true
```

Enable Signal Service publication:

```text
SignalRealtime:Enabled=true
SignalRealtime:TradingApiBaseUrl=http://localhost:5100
```

The secret must not be committed to `appsettings.json`.

## Trading API endpoints

- `GET /api/v1/stream/signals/status`
- `GET /api/v1/stream/signals/recent`
- `POST /internal/v1/signals/events` — protected internal ingestion
- `/hubs/signals` — SignalR client connection

## Safety boundary

This slice does not approve risk, create a trade plan, submit an order or call a broker. `CONSUMED` means a validated signal has been consumed by a downstream workflow; it does not mean an order was placed.
