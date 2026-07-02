SET NOCOUNT ON;

IF COL_LENGTH(N'risk.trade_plans', N'signal_risk_evaluation_id') IS NULL
    THROW 59301, 'risk.trade_plans.signal_risk_evaluation_id is missing.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE [parent_object_id] = OBJECT_ID(N'[risk].[trade_plans]')
      AND [name] = N'fk_trade_plans_signal_risk_evaluation'
)
    THROW 59302, 'Trade Plan to Signal Risk evaluation foreign key is missing.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'[risk].[trade_plans]')
      AND [name] = N'ck_trade_plans_risk_lineage'
)
    THROW 59303, 'Trade Plan Risk lineage exclusivity constraint is missing.', 1;

IF OBJECT_ID(N'[risk].[trade_plan_work_items]', N'U') IS NULL
    THROW 59304, 'risk.trade_plan_work_items is missing.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.key_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'[risk].[trade_plan_work_items]')
      AND [name] = N'uq_trade_plan_work_evaluation'
)
    THROW 59305, 'Evaluation idempotency constraint is missing.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[risk].[trade_plan_work_items]')
      AND [name] = N'ix_trade_plan_work_available'
)
    THROW 59306, 'Available-work index is missing.', 1;

PRINT 'V0022 Signal Risk to Trade Plan bridge verification passed.';
