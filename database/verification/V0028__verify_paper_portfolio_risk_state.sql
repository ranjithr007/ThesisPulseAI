SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[risk].[portfolio_risk_snapshots]', N'U') IS NULL
    THROW 59820, 'Missing risk.portfolio_risk_snapshots.', 1;
IF OBJECT_ID(N'[risk].[portfolio_control_states]', N'U') IS NULL
    THROW 59821, 'Missing risk.portfolio_control_states.', 1;
IF OBJECT_ID(N'[risk].[portfolio_risk_events]', N'U') IS NULL
    THROW 59822, 'Missing risk.portfolio_risk_events.', 1;
IF OBJECT_ID(N'[risk].[portfolio_risk_work_items]', N'U') IS NULL
    THROW 59823, 'Missing risk.portfolio_risk_work_items.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[risk].[portfolio_risk_snapshots]')
      AND [name] = N'uq_portfolio_risk_source_policy'
      AND [is_unique] = 1
)
    THROW 59824, 'Missing unique source-policy replay guard.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[risk].[portfolio_control_states]')
      AND [name] = N'uq_portfolio_control_state'
      AND [is_unique] = 1
)
    THROW 59825, 'Missing unique current portfolio control-state guard.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[risk].[portfolio_risk_work_items]')
      AND [name] = N'uq_portfolio_risk_work_source'
      AND [is_unique] = 1
)
    THROW 59826, 'Missing unique portfolio risk work replay guard.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[risk].[portfolio_risk_work_items]')
      AND [name] = N'ix_portfolio_risk_work_available'
)
    THROW 59827, 'Missing available-work index.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'[risk].[portfolio_risk_snapshots]')
      AND [name] = N'ck_portfolio_risk_permissions'
)
    THROW 59828, 'Missing portfolio risk permission constraint.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'[risk].[portfolio_risk_work_items]')
      AND [name] = N'ck_portfolio_risk_work_lease'
)
    THROW 59829, 'Missing leased-work ownership constraint.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [operations].[database_metadata]
    WHERE [database_metadata_id] = 1
      AND [schema_baseline_version] = 'V0028'
)
    THROW 59830, 'Database metadata baseline was not advanced to V0028.', 1;

SELECT
    'V0028_VERIFIED' AS [verification_status],
    SYSUTCDATETIME() AS [verified_at_utc];
