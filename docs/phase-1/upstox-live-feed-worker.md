# Phase 1 — Upstox V3 Live Feed Worker

## Scope

This slice activates the long-running Upstox Market Data Feed V3 transport while preserving the canonical Market Data boundary.

Implemented:

- official Market Data Feed V3 Protobuf schema;
- generated C# Protobuf message classes;
- secure authorized-URI retrieval for every connection attempt;
- one managed `ClientWebSocket` connection;
- binary V3 subscription commands;
- configuration-backed subscription snapshots;
- mode-specific subscription limit validation;
- fragmented binary frame assembly;
- Protobuf decoding and canonical feed normalization;
- automatic persistence through `IMarketDataStore`;
- market-status synchronization;
- open-market and closed-market silence controls;
- bounded exponential reconnect backoff with jitter;
- full subscription replay after every reconnect;
- connection and persistence health telemetry;
- focused executable tests.

## Provider boundary

Upstox-specific WebSocket, Protobuf, feed modes, subscription payloads and field mappings remain inside:

```text
src/ThesisPulse.Infrastructure.Brokers.Upstox
```

The worker emits only canonical `CanonicalLiveMarketUpdateV1` records to the shared Market Data store. It cannot call risk, execution or order APIs.

## Safety defaults

The worker is disabled by default.

```json
{
  "Upstox": {
    "Enabled": false,
    "LiveFeed": {
      "Enabled": false,
      "Mode": "full",
      "InstrumentKeys": []
    }
  }
}
```

Startup fails when the live worker is enabled without:

- the provider being enabled;
- at least one instrument key;
- a supported mode;
- valid resilience values;
- a subscription count within the selected mode limit.

The access token remains a runtime secret and is never written to logs, health responses or persistence.

## Supported modes and enforced limits

| Mode | Maximum instrument keys |
|---|---:|
| `ltpc` | 5,000 |
| `option_greeks` | 3,000 |
| `full` | 2,000 |
| `full_d30` | 50 |

A worker instance uses one mode for its entire subscription. Mixed-mode subscription allocation is deferred until a subscription planner is introduced.

## Local configuration

Configure SQL Server persistence before enabling the worker:

```powershell
dotnet user-secrets set `
  "ConnectionStrings:OperationalDatabase" `
  "<local SQL Server connection string>" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "MarketData:Persistence:Provider" `
  "SqlServer" `
  --project src/ThesisPulse.MarketData.Service
```

Configure the current Upstox access token:

```powershell
dotnet user-secrets set `
  "Upstox:AccessToken" `
  "<current-access-token>" `
  --project src/ThesisPulse.MarketData.Service
```

Enable the provider and worker:

```powershell
dotnet user-secrets set `
  "Upstox:Enabled" `
  "true" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "Upstox:LiveFeed:Enabled" `
  "true" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "Upstox:LiveFeed:Mode" `
  "full" `
  --project src/ThesisPulse.MarketData.Service
```

Set instrument keys using indexed configuration values:

```powershell
dotnet user-secrets set `
  "Upstox:LiveFeed:InstrumentKeys:0" `
  "NSE_INDEX|Nifty 50" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "Upstox:LiveFeed:InstrumentKeys:1" `
  "NSE_INDEX|Nifty Bank" `
  --project src/ThesisPulse.MarketData.Service
```

The corresponding canonical instrument mappings must already exist. Run instrument synchronization before enabling live persistence for new keys.

## Run

```powershell
dotnet run `
  --project src/ThesisPulse.MarketData.Service `
  --urls http://localhost:5101
```

Health endpoints:

```text
GET http://localhost:5101/api/v1/status
GET http://localhost:5101/api/v1/live-feed/health
```

Health states:

```text
DISABLED
STOPPED
STARTING
AUTHORIZING
CONNECTING
SUBSCRIBING
SYNCHRONIZING
STREAMING
RECONNECTING
```

The health response includes:

- connection and reconnect counts;
- selected mode and subscription count;
- connection, message, persistence and next-retry timestamps;
- received message and persisted update counts;
- the latest feed message type;
- market segment statuses;
- whether any subscribed market segment is open;
- the latest sanitized error.

## Connection lifecycle

For every connection attempt the worker:

1. requests a fresh authorized WebSocket URI;
2. validates that it uses `wss`;
3. connects within the configured timeout;
4. builds and sends a binary `sub` command;
5. waits for market synchronization and feed messages;
6. assembles fragmented messages up to the configured size limit;
7. decodes the official V3 Protobuf payload;
8. normalizes provider fields to canonical market updates;
9. persists through the configured Market Data store;
10. reconnects and replays the complete subscription after failure.

## Silence and reconnect policy

Default controls:

| Control | Default |
|---|---:|
| Connect timeout | 20 seconds |
| Open-market silence timeout | 45 seconds |
| Closed-market silence timeout | 300 seconds |
| WebSocket keep-alive | 20 seconds |
| Initial reconnect delay | 1 second |
| Maximum reconnect delay | 60 seconds |
| Stable connection reset threshold | 60 seconds |
| Receive buffer | 64 KB |
| Maximum assembled message | 4 MB |

Reconnect delays use exponential growth, a maximum cap and jitter. A connection that remains stable beyond the reset threshold clears accumulated failure pressure.

## Persistence behavior

Each decoded feed response is normalized into one canonical update per instrument and sent to `IMarketDataStore`.

SQL Server mode persists:

- ingestion batch lineage;
- immutable raw source observations;
- payload hashes;
- quality and freshness assessments;
- instrument ingestion cursors;
- accepted, duplicate and rejected counts.

Unknown instrument mappings are rejected by the store and reported through worker logs and health counters. The worker continues processing other instruments.

## Exit gate

This slice is complete when:

- the official V3 Protobuf schema generates successfully;
- subscription commands are sent as binary messages;
- fragmented Protobuf responses decode correctly;
- market information updates feed health;
- live and initial snapshots normalize into canonical updates;
- canonical updates persist automatically;
- message silence, close frames and decoding failures trigger reconnect;
- every reconnect obtains a new URI and replays the subscription;
- subscription limits are validated before service startup;
- all .NET, Python and React CI jobs pass.
