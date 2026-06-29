/*
Verification: V0006__verify_risk_and_trade_plan_tables.sql
Purpose:
  Verify V0006 risk-policy, snapshot, decision and trade-plan tables,
  trusted relationships, filtered indexes, contract checks, fixed precision
  and the V0006 database baseline marker.
Expected result:
  One PASS result set and no raised verification error.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @expected_tables TABLE
(
    [table_name] sysname NOT NULL PRIMARY KEY
);

INSERT INTO @expected_tables ([table_name])
VALUES
    (N'risk_policies'),
    (N'risk_policy_mandatory_rules'),
    (N'risk_policy_status_events'),
    (N'active_policy_assignments'),
    (N'capital_snapshots'),
    (N'portfolio_snapshots'),
    (N'portfolio_snapshot_positions'),
    (N'portfolio_snapshot_exposures'),
    (N'risk_decisions'),
    (N'risk_decision_reason_codes'),
    (N'risk_decision_targets'),
    (N'risk_decision_limit_checks'),
    (N'trade_plans'),
    (N'trade_plan_targets'),
    (N'trade_plan_status_events');

IF EXISTS
(
    SELECT 1
    FROM @expected_tables AS expected
    WHERE OBJECT_ID(N'[risk].' + QUOTENAME(expected.[table_name]), N'U') IS NULL
)
BEGIN
    SELECT expected.[table_name] AS [missing_table]
    FROM @expected_tables AS expected
    WHERE OBJECT_ID(N'[risk].' + QUOTENAME(expected.[table_name]), N'U') IS NULL;

    RAISERROR('V0006 risk table verification failed.', 16, 1);
    RETURN;
END;

DECLARE @expected_foreign_keys TABLE
(
    [table_name] nvarchar(300) NOT NULL,
    [foreign_key_name] sysname NOT NULL,
    PRIMARY KEY ([table_name], [foreign_key_name])
);

INSERT INTO @expected_foreign_keys ([table_name], [foreign_key_name])
VALUES
    (N'[risk].[risk_policies]', N'fk_risk_policies_parent'),
    (N'[risk].[risk_policy_mandatory_rules]', N'fk_risk_policy_rules_policy'),
    (N'[risk].[risk_policy_status_events]', N'fk_risk_policy_status_events_policy'),
    (N'[risk].[active_policy_assignments]', N'fk_active_policy_assignments_policy'),
    (N'[risk].[portfolio_snapshot_positions]', N'fk_portfolio_snapshot_positions_snapshot'),
    (N'[risk].[portfolio_snapshot_positions]', N'fk_portfolio_snapshot_positions_instrument'),
    (N'[risk].[portfolio_snapshot_exposures]', N'fk_portfolio_snapshot_exposures_snapshot'),
    (N'[risk].[risk_decisions]', N'fk_risk_decisions_signal'),
    (N'[risk].[risk_decisions]', N'fk_risk_decisions_thesis'),
    (N'[risk].[risk_decisions]', N'fk_risk_decisions_policy'),
    (N'[risk].[risk_decisions]', N'fk_risk_decisions_capital_snapshot'),
    (N'[risk].[risk_decisions]', N'fk_risk_decisions_portfolio_snapshot'),
    (N'[risk].[risk_decision_reason_codes]', N'fk_risk_decision_reasons_decision'),
    (N'[risk].[risk_decision_targets]', N'fk_risk_decision_targets_decision'),
    (N'[risk].[risk_decision_limit_checks]', N'fk_risk_decision_limit_checks_decision'),
    (N'[risk].[trade_plans]', N'fk_trade_plans_risk_decision'),
    (N'[risk].[trade_plans]', N'fk_trade_plans_thesis'),
    (N'[risk].[trade_plans]', N'fk_trade_plans_signal'),
    (N'[risk].[trade_plans]', N'fk_trade_plans_supersedes'),
    (N'[risk].[trade_plan_targets]', N'fk_trade_plan_targets_plan'),
    (N'[risk].[trade_plan_status_events]', N'fk_trade_plan_status_events_plan');

IF EXISTS
(
    SELECT 1
    FROM @expected_foreign_keys AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[foreign_key_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    )
)
BEGIN
    SELECT
        expected.[table_name],
        expected.[foreign_key_name] AS [missing_or_untrusted_foreign_key]
    FROM @expected_foreign_keys AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[foreign_key_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    );

    RAISERROR('V0006 foreign-key verification failed.', 16, 1);
    RETURN;
END;

DECLARE @expected_indexes TABLE
(
    [table_name] nvarchar(300) NOT NULL,
    [index_name] sysname NOT NULL,
    [must_be_unique] bit NOT NULL,
    [must_be_filtered] bit NOT NULL,
    PRIMARY KEY ([table_name], [index_name])
);

INSERT INTO @expected_indexes
(
    [table_name], [index_name], [must_be_unique], [must_be_filtered]
)
VALUES
    (N'[risk].[risk_policies]', N'ix_risk_policies_scope', 0, 0),
    (N'[risk].[risk_policies]', N'ix_risk_policies_parent', 0, 0),
    (N'[risk].[risk_policy_status_events]', N'ix_risk_policy_status_events_latest', 0, 0),
    (N'[risk].[active_policy_assignments]', N'ux_active_policy_assignments_open', 1, 1),
    (N'[risk].[active_policy_assignments]', N'ix_active_policy_assignments_policy', 0, 0),
    (N'[risk].[capital_snapshots]', N'ix_capital_snapshots_latest', 0, 0),
    (N'[risk].[portfolio_snapshots]', N'ix_portfolio_snapshots_latest', 0, 0),
    (N'[risk].[portfolio_snapshot_positions]', N'ix_portfolio_snapshot_positions_instrument', 0, 0),
    (N'[risk].[risk_decisions]', N'ix_risk_decisions_latest', 0, 0),
    (N'[risk].[risk_decisions]', N'ix_risk_decisions_policy', 0, 0),
    (N'[risk].[risk_decisions]', N'ix_risk_decisions_correlation', 0, 0),
    (N'[risk].[risk_decision_limit_checks]', N'ix_risk_decision_limit_checks_result', 0, 0),
    (N'[risk].[trade_plans]', N'ux_trade_plans_current_decision', 1, 1),
    (N'[risk].[trade_plans]', N'ix_trade_plans_latest', 0, 0),
    (N'[risk].[trade_plans]', N'ix_trade_plans_correlation', 0, 0),
    (N'[risk].[trade_plan_status_events]', N'ix_trade_plan_status_events_latest', 0, 0),
    (N'[risk].[trade_plan_status_events]', N'ix_trade_plan_status_events_status', 0, 0);

IF EXISTS
(
    SELECT 1
    FROM @expected_indexes AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS actual
        WHERE actual.[object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[index_name]
          AND actual.[is_unique] = expected.[must_be_unique]
          AND actual.[has_filter] = expected.[must_be_filtered]
          AND actual.[is_disabled] = 0
    )
)
BEGIN
    SELECT
        expected.[table_name],
        expected.[index_name] AS [missing_or_invalid_index]
    FROM @expected_indexes AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS actual
        WHERE actual.[object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[index_name]
          AND actual.[is_unique] = expected.[must_be_unique]
          AND actual.[has_filter] = expected.[must_be_filtered]
          AND actual.[is_disabled] = 0
    );

    RAISERROR('V0006 index verification failed.', 16, 1);
    RETURN;
END;

DECLARE @expected_checks TABLE
(
    [table_name] nvarchar(300) NOT NULL,
    [constraint_name] sysname NOT NULL,
    PRIMARY KEY ([table_name], [constraint_name])
);

INSERT INTO @expected_checks ([table_name], [constraint_name])
VALUES
    (N'[risk].[risk_policies]', N'ck_risk_policies_contract_version'),
    (N'[risk].[risk_policies]', N'ck_risk_policies_status'),
    (N'[risk].[risk_policies]', N'ck_risk_policies_environment'),
    (N'[risk].[risk_policies]', N'ck_risk_policies_scope'),
    (N'[risk].[risk_policies]', N'ck_risk_policies_core_limits'),
    (N'[risk].[risk_policies]', N'ck_risk_policies_soft_response'),
    (N'[risk].[risk_policies]', N'ck_risk_policies_hard_response'),
    (N'[risk].[risk_policies]', N'ck_risk_policies_consecutive_loss_response'),
    (N'[risk].[risk_policies]', N'ck_risk_policies_approval'),
    (N'[risk].[risk_policy_status_events]', N'ck_risk_policy_status_events_status'),
    (N'[risk].[active_policy_assignments]', N'ck_active_policy_assignments_scope'),
    (N'[risk].[active_policy_assignments]', N'ck_active_policy_assignments_status'),
    (N'[risk].[capital_snapshots]', N'ck_capital_snapshots_amounts'),
    (N'[risk].[capital_snapshots]', N'ck_capital_snapshots_time'),
    (N'[risk].[capital_snapshots]', N'ck_capital_snapshots_raw_json'),
    (N'[risk].[portfolio_snapshots]', N'ck_portfolio_snapshots_values'),
    (N'[risk].[portfolio_snapshots]', N'ck_portfolio_snapshots_time'),
    (N'[risk].[portfolio_snapshot_positions]', N'ck_portfolio_snapshot_positions_values'),
    (N'[risk].[portfolio_snapshot_exposures]', N'ck_portfolio_snapshot_exposures_values'),
    (N'[risk].[risk_decisions]', N'ck_risk_decisions_contract_version'),
    (N'[risk].[risk_decisions]', N'ck_risk_decisions_decision'),
    (N'[risk].[risk_decisions]', N'ck_risk_decisions_risk_values'),
    (N'[risk].[risk_decisions]', N'ck_risk_decisions_quantity'),
    (N'[risk].[risk_decisions]', N'ck_risk_decisions_decision_outcome'),
    (N'[risk].[risk_decisions]', N'ck_risk_decisions_current_limits'),
    (N'[risk].[risk_decisions]', N'ck_risk_decisions_time'),
    (N'[risk].[risk_decision_limit_checks]', N'ck_risk_decision_limit_checks_result'),
    (N'[risk].[trade_plans]', N'ck_trade_plans_contract_version'),
    (N'[risk].[trade_plans]', N'ck_trade_plans_entry_order_fields'),
    (N'[risk].[trade_plans]', N'ck_trade_plans_quantity'),
    (N'[risk].[trade_plans]', N'ck_trade_plans_stop_loss'),
    (N'[risk].[trade_plans]', N'ck_trade_plans_direction'),
    (N'[risk].[trade_plans]', N'ck_trade_plans_session_times'),
    (N'[risk].[trade_plans]', N'ck_trade_plans_status'),
    (N'[risk].[trade_plans]', N'ck_trade_plans_version_lineage'),
    (N'[risk].[trade_plan_targets]', N'ck_trade_plan_targets_values'),
    (N'[risk].[trade_plan_status_events]', N'ck_trade_plan_status_events_status');

IF EXISTS
(
    SELECT 1
    FROM @expected_checks AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[constraint_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    )
)
BEGIN
    SELECT
        expected.[table_name],
        expected.[constraint_name] AS [missing_or_untrusted_check]
    FROM @expected_checks AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[constraint_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    );

    RAISERROR('V0006 check-constraint verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[risk].[risk_policies]')
      AND columns.[name] IN
      (
          N'standard_risk_per_trade_fraction',
          N'maximum_risk_per_trade_fraction',
          N'maximum_total_open_risk_fraction',
          N'daily_soft_loss_fraction',
          N'daily_hard_loss_fraction',
          N'weekly_loss_fraction',
          N'maximum_strategy_drawdown_fraction',
          N'maximum_portfolio_drawdown_fraction'
      )
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 8
       AND MIN
       (
           CASE
               WHEN types.[name] = N'decimal'
                AND columns.[precision] = 9
                AND columns.[scale] = 8
               THEN 1 ELSE 0
           END
       ) = 1
)
BEGIN
    RAISERROR('V0006 risk-policy fraction precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[risk].[risk_decisions]')
      AND columns.[name] IN
      (
          N'requested_risk_amount', N'approved_risk_amount',
          N'requested_quantity', N'approved_quantity',
          N'entry_price', N'stop_loss_price'
      )
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 6
       AND MIN
       (
           CASE
               WHEN types.[name] = N'decimal'
                AND columns.[precision] = 19
                AND columns.[scale] = 6
               THEN 1 ELSE 0
           END
       ) = 1
)
BEGIN
    RAISERROR('V0006 risk-decision amount precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[risk].[trade_plans]')
      AND columns.[name] IN
      (
          N'entry_reference_price', N'approved_quantity',
          N'stop_loss_price', N'maximum_slippage_fraction'
      )
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 4
       AND SUM
       (
           CASE
               WHEN columns.[name] = N'maximum_slippage_fraction'
                AND types.[name] = N'decimal'
                AND columns.[precision] = 9
                AND columns.[scale] = 8
               THEN 1
               WHEN columns.[name] <> N'maximum_slippage_fraction'
                AND types.[name] = N'decimal'
                AND columns.[precision] = 19
                AND columns.[scale] = 6
               THEN 1
               ELSE 0
           END
       ) = 4
)
BEGIN
    RAISERROR('V0006 trade-plan precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [operations].[database_metadata]
    WHERE [database_metadata_id] = 1
      AND [schema_baseline_version] = 'V0006'
)
BEGIN
    RAISERROR('Database metadata was not advanced to V0006.', 16, 1);
    RETURN;
END;

SELECT
    'PASS' AS [verification_status],
    'V0006' AS [migration_version],
    DB_NAME() AS [database_name],
    (SELECT COUNT_BIG(*) FROM @expected_tables) AS [verified_table_count],
    (SELECT COUNT_BIG(*) FROM @expected_foreign_keys) AS [verified_foreign_key_count],
    (SELECT COUNT_BIG(*) FROM @expected_indexes) AS [verified_index_count],
    (SELECT COUNT_BIG(*) FROM @expected_checks) AS [verified_check_count];
