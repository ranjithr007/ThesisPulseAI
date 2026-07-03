# Windows local development

ThesisPulse AI Phase 5.6 provides a supported PowerShell workflow for starting the complete local PAPER application without Docker.

## Safety model

- The launcher forces the PAPER environment.
- Live execution remains disabled.
- Database migrations are opt-in.
- No database reset or secret generation is performed.
- Shutdown is limited to processes recorded by the launcher.

## Prerequisites

Install these tools on Windows:

- Git for Windows;
- .NET 8 SDK or later;
- Node.js 22 or later with npm;
- Windows PowerShell 5.1 or PowerShell 7;
- SQL Server when SQL-backed persistence or migrations are required.

Visual Studio 2022 remains supported for individual projects. PowerShell is the supported path for starting the complete stack.

## Validate the workstation

From the repository root:

```powershell
.\scripts\dev\Test-ThesisPulsePrerequisites.ps1
```

This checks tools, versions, required files, project paths, duplicate ports, and occupied ports. It does not restore packages, change the database, or start processes.

## Start the PAPER stack

```powershell
.\scripts\dev\Start-ThesisPulse.ps1
```

The launcher restores and builds the .NET solution, runs `npm ci`, starts eight backend services and the React frontend, checks readiness, writes logs, and opens `http://localhost:5173`.

To keep the browser closed:

```powershell
.\scripts\dev\Start-ThesisPulse.ps1 -NoBrowser
```

For a later startup when dependencies and build outputs are already current:

```powershell
.\scripts\dev\Start-ThesisPulse.ps1 -SkipRestore -SkipBuild -SkipFrontendInstall
```

## Optional database migrations

Set `THESISPULSE_DATABASE_CONNECTION` in the current PowerShell session using the approved local SQL Server connection string, then run:

```powershell
.\scripts\dev\Start-ThesisPulse.ps1 -RunMigrations
```

The migrator applies versioned files under `database/migrations` in PAPER mode. It does not automatically reset the database and does not print the connection string.

## Stop the stack

```powershell
.\scripts\dev\Stop-ThesisPulse.ps1
```

The launcher records process IDs and start times in `.thesispulse-dev/processes.json`. The stop command validates both values before terminating a recorded process.

## Logs

Runtime files are written under `.thesispulse-dev/`:

```text
.thesispulse-dev/
  processes.json
  logs/
    trading-api.stdout.log
    trading-api.stderr.log
    market-data.stdout.log
    market-data.stderr.log
    ...
```

When readiness fails, the launcher reports the exact stdout and stderr files to inspect and cleans up processes it already started.

## Local addresses

| Component | Address |
|---|---|
| React frontend | `http://localhost:5173` |
| Trading API | `http://localhost:60515` |
| Market Data Service | `http://localhost:5101` |
| Signal Service | `http://localhost:59479` |
| Thesis Service | `http://localhost:59475` |
| Risk Service | `http://localhost:59477` |
| Execution Service | `http://localhost:59482` |
| Portfolio Service | `http://localhost:59483` |
| Operations Service | `http://localhost:59485` |

Every backend is checked through `/health/ready`.

The browser uses same-origin `/local/*` paths. Vite proxies those paths to the backend ports, including websocket traffic.

## Common failures

### Git is not recognized

Install Git for Windows with command-line PATH support, reopen PowerShell, and verify:

```powershell
git --version
```

### A port is occupied

The prerequisite script reports the exact port. Run the ThesisPulse stop command first when the stack was started by the launcher. Otherwise inspect the port owner before changing anything.

### A process manifest already exists

Run the stop command. If the recorded process no longer matches, inspect `.thesispulse-dev/processes.json` before removing a stale manifest manually.

### A service misses its readiness deadline

Read the log files reported by the launcher. Common causes include invalid SQL configuration, a migration mismatch, or an enabled worker whose dependency is unavailable.

A longer bounded readiness window can be selected:

```powershell
.\scripts\dev\Start-ThesisPulse.ps1 -StartupTimeoutSeconds 180
```

### `npm ci` fails

Confirm that `frontend-react/package-lock.json` exists and that Node.js 22 or later is active.

## Configuration validation

Run the non-launching Phase 5.6 contract check with:

```powershell
.\scripts\dev\Test-ThesisPulseDevelopmentConfiguration.ps1
```

It validates PowerShell syntax, project mappings, unique ports, readiness paths, frontend proxy paths, Vite targets, and required PAPER workspace variables. CI also runs the .NET solution build and React typecheck/build.
