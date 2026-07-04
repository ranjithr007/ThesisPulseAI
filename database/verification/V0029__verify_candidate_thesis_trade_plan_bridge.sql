SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[risk].[trade_plans]', N'U') IS NULL
    THROW 59920, 'Missing risk.trade_plans.', 1;

IF COL_LENGTH(N'risk.trade_plans', N'candidate_thesis_uid') IS NULL
    THROW 59921, 'Missing risk.trade_plans.candidate_thesis_uid.', 1;

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[risk].[trade_plans]')
      AND [name] = N'thesis_id'
      AND [is_nullable] = 0
)
    THROW 59922, 'risk.trade_plans.thesis_id must be nullable for candidate-thesis lineage.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'[risk].[trade_plans]')
      AND [name] = N'ck_trade_plans_thesis_lineage'
)
    THROW 59923, 'Missing mutually exclusive thesis-lineage constraint.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[risk].[trade_plans]')
      AND [name] = N'ux_trade_plans_candidate_thesis_version'
      AND [is_unique] = 1
      AND [has_filter] = 1
)
    THROW 59924, 'Missing candidate-thesis version replay guard.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[risk].[trade_plans]')
      AND [name] = N'ux_trade_plans_current_candidate_thesis'
      AND [is_unique] = 1
      AND [has_filter] = 1
)
    THROW 59925, 'Missing current candidate-thesis guard.', 1;

IF EXISTS
(
    SELECT 1
    FROM [risk].[trade_plans]
    WHERE ([thesis_id] IS NULL AND [candidate_thesis_uid] IS NULL)
       OR ([thesis_id] IS NOT NULL AND [candidate_thesis_uid] IS NOT NULL)
)
    THROW 59926, 'Existing trade-plan thesis lineage violates the bridge invariant.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [operations].[database_metadata]
    WHERE [database_metadata_id] = 1
      AND [schema_baseline_version] = 'V0029'
)
    THROW 59927, 'Database metadata baseline was not advanced to V0029.', 1;

SELECT
    'V0029_VERIFIED' AS [verification_status],
    SYSUTCDATETIME() AS [verified_at_utc];
