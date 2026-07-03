# Phase 4.10 — Controlled End-to-End Activation

## Purpose

Phase 4.10 activates the SQL-to-Python option-chain intelligence path only after configuration, secret, queue-recovery, and operational-smoke gates pass. It does not grant signal, risk, contract-selection, trade-plan, broker-order, or market-execution authority.

## Required secret-based configuration

Set secrets through the deployment platform. Do not commit them to `appsettings.json`.

```text
OptionChainRuntime__ConnectionString=<SQL Server connection string>
OptionChainRuntime__PythonInternalApiKey=<shared internal API key>
```

Set the following non-secret environment configuration for a controlled activation:

```text
ProductionActivation__Mode=CONTROLLED
ProductionActivation__OperationalSmokeEnabled=true
OptionChainRuntime__Enabled=true
OptionChainRuntime__WorkerDiscoveryEnabled=true
OptionChainRuntime__WorkerExecutionEnabled=true
OptionChainRuntime__PersistenceProvider=SqlServer
OptionChainRuntime__PythonServiceBaseUrl=https://<python-service>
```

`Platform__LiveExecutionEnabled` must remain `false`. No trading or order-routing component is enabled by this phase.

## Activation sequence

1. Deploy SQL migrations and verify the canonical option-chain snapshot, contract-entry, evidence, and durable work tables.
2. Deploy the Python deterministic intelligence service with the matching internal API key.
3. Deploy the Signal Service with runtime, discovery, and worker execution still disabled.
4. Add secrets and non-secret production configuration.
5. Enable `ProductionActivation__OperationalSmokeEnabled` and call the readiness endpoint.
6. Enable runtime, discovery, and worker execution in that order.
7. Run `scripts/phase410-operational-smoke.ps1`.
8. Observe worker metrics and verify one created/revised result, one duplicate replay, and one retry/recovery cycle.

## Readiness endpoints

- `GET /api/v1/internal/option-chain/production-readiness`
- `POST /api/v1/internal/option-chain/operational-smoke`

The response never returns secret values. A blocked gate returns HTTP 503 with stable blocking reason codes.

## Queue recovery validation

Use a non-production test snapshot and temporarily stop the Python service after the work item is leased. Verify:

1. the failure is classified retryable;
2. the work item returns to the retry schedule;
3. the lease expires without creating a second active owner;
4. processing resumes after Python recovery;
5. the durable work UID remains the source message UID;
6. replay produces `DUPLICATE` rather than a second intelligence output.

Queue recovery policy is considered configured only when attempts, lease duration, initial retry, and maximum retry form a bounded valid policy.

## Rollback

Disable in reverse order:

```text
OptionChainRuntime__WorkerExecutionEnabled=false
OptionChainRuntime__WorkerDiscoveryEnabled=false
OptionChainRuntime__Enabled=false
ProductionActivation__Mode=DISABLED
```

Existing snapshots, work history, and intelligence lineage remain available for audit. Disabling this path has no effect on trading because execution authority remains false.
