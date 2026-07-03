# Option-Chain Rollout Operations Runbook

## Deployment order

1. Apply `database/migrations/phase414_option_chain_rollout_operations.sql`.
2. Confirm the rollout audit, scheduler lease, and scheduler run tables exist.
3. Deploy Signal Service with `OptionChainSqlOperations:Enabled` still false.
4. Configure `OptionChainSqlOperations__DatabaseConnection` through the secret store.
5. Restart one instance and verify `/api/v1/internal/option-chain/operations/readiness` returns `DISABLED` or `READY`.
6. Enable SQL operations on one instance first.
7. Confirm durable state restoration and fresh scheduler heartbeat.
8. Enable additional instances only after database lease behavior is verified.

## Required configuration

- `OptionChainSqlOperations__Enabled=true`
- `OptionChainSqlOperations__DatabaseConnection=<secret>`
- `OptionChainSqlOperations__InstanceName=<unique-instance-name>`
- `OptionChainOperations__OperatorApiKey=<secret>`
- `OptionChainOperations__SchedulerEnabled=true` only after readiness succeeds.

## Readiness gates

Production rollout must remain blocked unless all are true:

- SQL connectivity succeeds.
- Required tables exist.
- Durable rollout state is restored.
- Scheduler heartbeat is fresh when scheduling is enabled.
- Selection authority is false.
- Execution authority is false.

## Rollback

1. Disable `OptionChainOperations__SchedulerEnabled`.
2. Call the authenticated rollback endpoint with a unique command key and current version.
3. Verify the durable state reports `ROLLED_BACK`.
4. Confirm worker execution is suppressed.
5. Keep SQL persistence enabled so audit state and leases remain observable.

## Multi-instance operation

Each instance requires a unique instance name. Database leases prevent concurrent execution of the same maintenance job. Expired leases may be taken over after the configured lease duration. Operators should investigate repeated `SKIPPED_LEASE_HELD` outcomes or stale heartbeats.

## Validation

Run:

```bash
bash tests/option-chain/phase417_acceptance.sh
dotnet build ThesisPulseAI.sln --configuration Release
```
