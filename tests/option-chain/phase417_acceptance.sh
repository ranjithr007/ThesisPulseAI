#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

require() {
  local pattern="$1"
  local file="$2"
  grep -F "$pattern" "$file" >/dev/null
}

require "RestoreAsync" src/ThesisPulse.Signal.Service/OptionChainRolloutOperations.cs
require "STALE_ROLLOUT_VERSION" src/ThesisPulse.Signal.Service/OptionChainRolloutOperations.cs
require "FixedTimeEquals" src/ThesisPulse.Signal.Service/OptionChainRolloutOperations.cs
require "ROLLOUT_COMMAND_OR_VERSION_CONFLICT" src/ThesisPulse.Signal.Service/SqlServerOptionChainRolloutAuditStore.cs
require "WITH (HOLDLOCK)" src/ThesisPulse.Signal.Service/SqlServerOptionChainSchedulerStore.cs
require "expires_at_utc <= @observed_at_utc" src/ThesisPulse.Signal.Service/SqlServerOptionChainSchedulerStore.cs
require "new_version < (SELECT ISNULL(MAX(new_version), 0)" src/ThesisPulse.Signal.Service/SqlServerOptionChainRolloutAuditStore.cs
require "StatusCodes.Status503ServiceUnavailable" src/ThesisPulse.Signal.Service/OptionChainSqlReadiness.cs
require "selectionAuthority" src/ThesisPulse.Signal.Service/OptionChainSqlReadiness.cs
require "executionAuthority" src/ThesisPulse.Signal.Service/OptionChainSqlReadiness.cs
require "thesispulse_option_chain_operations_events" src/ThesisPulse.Signal.Service/OptionChainOperationsMetrics.cs

echo "Phase 4.17 rollout operations acceptance checks passed."
