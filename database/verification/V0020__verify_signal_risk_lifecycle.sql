IF OBJECT_ID(N'[risk].[signal_risk_evaluations]', N'U') IS NULL
    THROW 52020, 'risk.signal_risk_evaluations is missing.', 1;

IF OBJECT_ID(N'[risk].[signal_risk_status_events]', N'U') IS NULL
    THROW 52021, 'risk.signal_risk_status_events is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'uq_signal_risk_command_uid'
      AND [object_id] = OBJECT_ID(N'[risk].[signal_risk_evaluations]')
)
    THROW 52022, 'Signal risk command idempotency constraint is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'uq_signal_risk_event_sequence'
      AND [object_id] = OBJECT_ID(N'[risk].[signal_risk_status_events]')
)
    THROW 52023, 'Signal risk status sequence constraint is missing.', 1;
