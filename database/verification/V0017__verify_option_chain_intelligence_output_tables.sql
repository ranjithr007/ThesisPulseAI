SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

DECLARE @required_tables TABLE ([object_name] sysname NOT NULL);
INSERT INTO @required_tables ([object_name])
VALUES
    (N'[intelligence].[option_chain_output_snapshot_inputs]'),
    (N'[intelligence].[option_chain_output_expiries]'),
    (N'[intelligence].[option_chain_output_walls]'),
    (N'[intelligence].[option_chain_output_oi_flows]'),
    (N'[intelligence].[option_chain_output_max_pain_points]'),
    (N'[intelligence].[option_chain_output_iv_term_points]');

IF EXISTS
(
    SELECT 1
    FROM @required_tables
    WHERE OBJECT_ID([object_name], N'U') IS NULL
)
    THROW 61751, 'One or more V0017 option-chain intelligence tables are missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE [name] = N'ck_engine_outputs_timeframe'
      AND [definition] LIKE '%OPTION_CHAIN%'
)
    THROW 61752, 'engine_outputs timeframe constraint does not allow OPTION_CHAIN.', 1;

IF
(
    SELECT COUNT(*)
    FROM sys.foreign_keys
    WHERE [name] IN
    (
        N'fk_option_chain_output_snapshot_inputs_output',
        N'fk_option_chain_output_snapshot_inputs_snapshot',
        N'fk_option_chain_output_expiries_output',
        N'fk_option_chain_output_expiries_snapshot_uid',
        N'fk_option_chain_output_walls_expiry',
        N'fk_option_chain_output_oi_flows_expiry',
        N'fk_option_chain_output_oi_flows_contract_uid',
        N'fk_option_chain_output_max_pain_expiry',
        N'fk_option_chain_output_iv_term_output',
        N'fk_option_chain_output_iv_term_snapshot_uid'
    )
      AND [is_disabled] = 0
      AND [is_not_trusted] = 0
) <> 10
    THROW 61753, 'One or more V0017 foreign keys are missing or untrusted.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[intelligence].[option_chain_output_snapshot_inputs]')
      AND [name] = N'ix_option_chain_output_snapshot_inputs_output'
)
    THROW 61754, 'Option-chain snapshot-input output index is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[intelligence].[option_chain_output_expiries]')
      AND [name] = N'ix_option_chain_output_expiries_lookup'
)
    THROW 61755, 'Option-chain expiry lookup index is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[intelligence].[option_chain_output_oi_flows]')
      AND [name] = N'ix_option_chain_output_oi_flows_state'
)
    THROW 61756, 'Option-chain OI-flow state index is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[intelligence].[option_chain_output_iv_term_points]')
      AND [name] = N'ix_option_chain_output_iv_term_curve'
)
    THROW 61757, 'Option-chain IV term-curve index is missing.', 1;

IF EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE [parent_object_id] IN
    (
        OBJECT_ID(N'[intelligence].[option_chain_output_snapshot_inputs]'),
        OBJECT_ID(N'[intelligence].[option_chain_output_expiries]'),
        OBJECT_ID(N'[intelligence].[option_chain_output_walls]'),
        OBJECT_ID(N'[intelligence].[option_chain_output_oi_flows]'),
        OBJECT_ID(N'[intelligence].[option_chain_output_max_pain_points]'),
        OBJECT_ID(N'[intelligence].[option_chain_output_iv_term_points]')
    )
      AND [is_disabled] = 1
)
    THROW 61758, 'One or more V0017 check constraints are disabled.', 1;

PRINT 'PASS V0017 option-chain intelligence output tables';
GO
