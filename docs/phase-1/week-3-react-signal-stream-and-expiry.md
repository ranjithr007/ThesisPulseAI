# Week 3 — React Signal Stream and Automatic Expiry

## Delivered flow

1. Python creates a versioned PAPER signal.
2. Signal Service validates and persists it.
3. Signal Service publishes a summary to Trading API.
4. Trading API broadcasts `signalUpdated` through SignalR.
5. The React scanner merges the real-time event into its current signal snapshot.
6. When SignalR is unavailable, the scanner automatically refreshes Signal Service through REST.
7. Operations Service periodically asks Signal Service to expire overdue `CANDIDATE` and `VALIDATED` signals.
8. Signal Service owns the expiry transaction and broadcasts every committed expiry.

## React configuration

The scanner uses these optional Vite variables:

```text
VITE_SIGNAL_API_BASE_URL=http://localhost:5102
VITE_TRADING_API_BASE_URL=http://localhost:5100
```

Defaults match the values above, so a local `.env` file is optional.

The scanner provides:

- SignalR connection and reconnect status;
- REST snapshot fallback every 15 seconds while disconnected;
- recent stream-event recovery after page refresh;
- lifecycle, direction, timeframe and text filters;
- signal strength and confidence meters;
- validity countdown and expiry visibility;
- PAPER-only environment banner.

## Automatic expiry ownership

`Operations.Service` owns scheduling, but it never writes Signal Service tables.

The worker calls:

```text
POST /internal/v1/signals/expire-due
```

Signal Service then:

- selects overdue signals under a SQL transaction;
- permits only current `CANDIDATE` or `VALIDATED` signals;
- appends `EXPIRED` status events;
- uses reason code `VALIDITY_WINDOW_ELAPSED`;
- commits before broadcasting;
- leaves risk and execution untouched.

## Local secret setup

Generate one local maintenance secret and configure it in both services:

```powershell
$maintenanceKey = [guid]::NewGuid().ToString("N")

dotnet user-secrets set `
  "SignalMaintenance:InternalApiKey" `
  $maintenanceKey `
  --project src/ThesisPulse.Signal.Service

dotnet user-secrets set `
  "SignalExpiry:InternalApiKey" `
  $maintenanceKey `
  --project src/ThesisPulse.Operations.Service
```

Enable maintenance and scheduling:

```powershell
dotnet user-secrets set `
  "SignalMaintenance:Enabled" `
  "true" `
  --project src/ThesisPulse.Signal.Service

dotnet user-secrets set `
  "SignalExpiry:Enabled" `
  "true" `
  --project src/ThesisPulse.Operations.Service
```

The real-time Signal Service to Trading API key remains separately controlled by:

- `SignalRealtime:InternalApiKey`
- `SignalStream:InternalApiKey`

No key belongs in source control or `appsettings.json`.

## Local startup

Start the services in separate Visual Studio profiles or terminals:

```powershell
dotnet run --project src/ThesisPulse.Trading.Api --urls http://localhost:5100
dotnet run --project src/ThesisPulse.Signal.Service --urls http://localhost:5102
dotnet run --project src/ThesisPulse.Operations.Service --urls http://localhost:5107

cd frontend-react
npm install
npm run dev
```

Open the Vite URL, normally `http://localhost:5173`.

## Verification endpoints

```text
GET http://localhost:5100/api/v1/stream/signals/status
GET http://localhost:5100/api/v1/stream/signals/recent
GET http://localhost:5102/api/v1/signals
GET http://localhost:5107/api/v1/jobs/signal-expiry
```

The Operations response exposes the last run state, timestamps, correlation ID, selected count, expired count, publication count and any error.

## Safety boundary

- Scheduler and maintenance are disabled until explicitly enabled with secrets.
- Only PAPER signals are in scope.
- Expiry does not create a risk decision or order intent.
- Operations Service has no direct write access to Signal Service persistence.
- A real-time broadcast failure does not roll back a committed expiry.
