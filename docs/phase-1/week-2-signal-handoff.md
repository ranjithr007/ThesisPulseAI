# Week 2 — Versioned Signal Handoff

## Implemented flow

1. The Python AI platform creates a deterministic PAPER signal at `POST /api/v1/signals/mock`.
2. The response is a versioned `signal.generated.v1` event envelope.
3. The envelope is submitted unchanged to `.NET Signal.Service` at `POST /api/v1/signals/intake`.
4. Signal Service validates message metadata and the canonical signal payload.
5. `InboxMessageProcessor` acquires the message through `IInboxStore`.
6. A duplicate message is acknowledged without processing it twice.
7. Successful processing records the inbox completion state and adds the signal to the Phase 1 local registry.
8. Accepted signals are visible through `GET /api/v1/signals`.

## Local startup

Start the Python platform:

```powershell
cd ai-python
python -m uvicorn app.main:app --port 8000
```

Start Signal Service in a second terminal:

```powershell
dotnet run --project src/ThesisPulse.Signal.Service --urls http://localhost:5102
```

## Demonstration

Create the mock envelope:

```powershell
$correlationId = [guid]::NewGuid().ToString()
$headers = @{ "X-Correlation-ID" = $correlationId }
$mockBody = @{
    instrumentKey = "NSE_INDEX|Nifty 50"
    direction = "LONG"
    primaryTimeframe = "5m"
    referencePrice = 25000
} | ConvertTo-Json

$envelope = Invoke-RestMethod `
    -Method Post `
    -Uri "http://localhost:8000/api/v1/signals/mock" `
    -Headers $headers `
    -ContentType "application/json" `
    -Body $mockBody
```

Submit the same envelope to Signal Service:

```powershell
$result = Invoke-RestMethod `
    -Method Post `
    -Uri "http://localhost:5102/api/v1/signals/intake" `
    -Headers $headers `
    -ContentType "application/json" `
    -Body ($envelope | ConvertTo-Json -Depth 20)

$result | ConvertTo-Json
```

Submitting the identical envelope again returns `DUPLICATE_IGNORED`.

## Provider selection

Local development defaults to:

```json
{
  "Messaging": {
    "Provider": "InMemory"
  }
}
```

To use the Phase 0 SQL Server inbox/outbox tables, supply the operational connection string through user secrets or the secret provider and set `Messaging:Provider` to `SqlServer`. Connection strings must never be committed.

## Current boundary

The intake registry is an interim Phase 1 read model. Durable signal-domain persistence into `intelligence.signals` will be introduced separately after instrument and engine identity resolution are wired. Inbox/outbox state is already durable when the SQL Server provider is selected.
