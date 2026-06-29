# Week 2 — Durable Signal Persistence

## Scope

Signal Service can now persist accepted versioned signals into the Phase 0 canonical intelligence schema.

The SQL Server provider writes one transaction containing:

- `intelligence.signals`
- `intelligence.signal_confirmation_timeframes`
- `intelligence.signal_evidence`
- the sequence-zero `intelligence.signal_status_events` record

If any insert fails, the entire signal transaction is rolled back.

## Identity resolution

### Creator engine

The configured engine must:

- match `SignalPersistence:CreatorEngineCode`;
- be active;
- have the `FUSION` role;
- have signal creation authority;
- have no order execution authority;
- be owned by the event metadata producer.

The local PAPER seed registers `THESIS_PULSE_MOCK_FUSION`, owned by `ThesisPulse.AI`.

### Instrument

The incoming `instrumentKey` is resolved against active reference data.

Supported key examples:

- `NSE|NIFTY50`
- `NSE_INDEX|Nifty 50`

The prefix before an underscore is treated as the exchange code. The value after the pipe may match either the canonical symbol or display name. Validity dates are checked against the signal generation date.

## Idempotency

Both identifiers are protected:

- `message_uid`
- `signal_uid`

A repeated message returns `DUPLICATE_MESSAGE_IGNORED`. A new message carrying an existing signal identity returns `DUPLICATE_SIGNAL_IGNORED`.

## Configuration

Local development remains in-memory by default:

```json
{
  "Messaging": {
    "Provider": "InMemory"
  },
  "SignalPersistence": {
    "Provider": "InMemory"
  }
}
```

For durable local PAPER operation, set both providers to `SqlServer` and supply `ConnectionStrings:OperationalDatabase` through user secrets or the configured secret provider:

```powershell
dotnet user-secrets set `
  "ConnectionStrings:OperationalDatabase" `
  "<local SQL Server connection string>" `
  --project src/ThesisPulse.Signal.Service
```

Then override:

```json
{
  "Messaging": {
    "Provider": "SqlServer"
  },
  "SignalPersistence": {
    "Provider": "SqlServer",
    "CreatorEngineCode": "THESIS_PULSE_MOCK_FUSION"
  }
}
```

Connection strings must not be committed.

## Required local seed

Run the local PAPER seed pack after migrations. The pack now includes:

- the index context instruments;
- the mock fusion engine authorization;
- verification of the engine authority.

## Safety boundary

Persisted signals start as `CANDIDATE`. This slice does not validate them for trading, create trade plans, approve risk, or place orders. Index context instruments remain non-tradable even when signals are stored against them.

## Next slice

Publish persisted signal summaries to Trading API subscribers and add durable signal status transitions for validation, rejection, expiry, supersession, and consumption.
