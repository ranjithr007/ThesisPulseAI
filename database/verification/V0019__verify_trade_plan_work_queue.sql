SET NOCOUNT ON;

IF OBJECT_ID(N'[risk].[trade_plan_work_items]', N'U') IS NULL
    THROW 59101, 'risk.trade_plan_work_items is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[risk].[trade_plan_work_items]')
      AND [name] = N'ix_trade_plan_work_available'
)
    THROW 59102, 'ix_trade_plan_work_available is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'[risk].[trade_plan_work_items]')
      AND [name] = N'uq_trade_plan_work_risk_decision'
)
    THROW 59103, 'Risk-decision idempotency constraint is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'[risk].[trade_plan_work_items]')
      AND [name] = N'ck_trade_plan_work_lease'
)
    THROW 59104, 'Lease consistency constraint is missing.', 1;

PRINT 'V0019 trade-plan work queue verification passed.';
