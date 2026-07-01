IF OBJECT_ID(N'[risk].[signal_risk_evaluations]', N'U') IS NULL
    THROW 52020, 'risk.signal_risk_evaluations is missing.', 1;

IF OBJECT_ID(N'[risk].[signal_risk_status_events]', N'U') IS NULL
    THROW 52021, 'risk.signal_risk_status_events is missing.', 1;

IF COL_LENGTH(N'risk.signal_risk_evaluations', N'risk_decision_uid') IS NULL
    THROW 52022, 'risk_decision_uid is missing.', 1;

IF COL_LENGTH(N'risk.signal_risk_evaluations', N'decision_snapshot_json') IS NULL
    THROW 52023, 'decision_snapshot_json is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[risk].[signal_risk_evaluations]')
      AND [is_unique] = 1
      AND [name] LIKE N'UQ__signal_r%'
)
    PRINT 'Unique constraints are present with SQL Server generated names; verify command_uid and request_uid in schema inspection.';

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE [parent_object_id] = OBJECT_ID(N'[risk].[signal_risk_status_events]')
      AND [name] = N'fk_signal_risk_event_evaluation'
)
    THROW 52024, 'Status-event evaluation foreign key is missing.', 1;
