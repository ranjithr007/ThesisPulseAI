/*
Verification: V0008__verify_portfolio_and_pnl_tables.sql
Purpose:
  Verify V0008 portfolio, position, lot, cash, exposure, valuation, P&L and
  position-reconciliation tables, trusted relationships, filtered indexes,
  fixed precision, rowversion projections and the V0008 baseline marker.
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
    (N'portfolios'),
    (N'positions'),
    (N'position_events'),
    (N'position_lots'),
    (N'position_lot_closures'),
    (N'realized_pnl_entries'),
    (N'cash_balances'),
    (N'cash_ledger_entries'),
    (N'exposure_states'),
    (N'exposure_ledger_entries'),
    (N'valuation_marks'),
    (N'position_valuations'),
    (N'pnl_snapshots'),
    (N'pnl_snapshot_positions'),
    (N'broker_position_observations'),
    (N'position_reconciliation_states'),
    (N'position_reconciliation_events');

IF EXISTS
(
    SELECT 1
    FROM @expected_tables AS expected
    WHERE OBJECT_ID(N'[portfolio].' + QUOTENAME(expected.[table_name]), N'U') IS NULL
)
BEGIN
    SELECT expected.[table_name] AS [missing_table]
    FROM @expected_tables AS expected
    WHERE OBJECT_ID(N'[portfolio].' + QUOTENAME(expected.[table_name]), N'U') IS NULL;

    RAISERROR('V0008 portfolio table verification failed.', 16, 1);
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
    (N'[portfolio].[portfolios]', N'fk_portfolios_broker_account'),
    (N'[portfolio].[positions]', N'fk_positions_portfolio'),
    (N'[portfolio].[positions]', N'fk_positions_instrument'),
    (N'[portfolio].[position_events]', N'fk_position_events_position'),
    (N'[portfolio].[position_events]', N'fk_position_events_fill'),
    (N'[portfolio].[position_events]', N'fk_position_events_order'),
    (N'[portfolio].[position_events]', N'fk_position_events_reconciliation_run'),
    (N'[portfolio].[position_lots]', N'fk_position_lots_position'),
    (N'[portfolio].[position_lots]', N'fk_position_lots_opening_fill'),
    (N'[portfolio].[position_lot_closures]', N'fk_position_lot_closures_lot'),
    (N'[portfolio].[position_lot_closures]', N'fk_position_lot_closures_fill'),
    (N'[portfolio].[position_lot_closures]', N'fk_position_lot_closures_event'),
    (N'[portfolio].[realized_pnl_entries]', N'fk_realized_pnl_entries_portfolio'),
    (N'[portfolio].[realized_pnl_entries]', N'fk_realized_pnl_entries_position'),
    (N'[portfolio].[realized_pnl_entries]', N'fk_realized_pnl_entries_closure'),
    (N'[portfolio].[realized_pnl_entries]', N'fk_realized_pnl_entries_fill'),
    (N'[portfolio].[cash_balances]', N'fk_cash_balances_portfolio'),
    (N'[portfolio].[cash_ledger_entries]', N'fk_cash_ledger_entries_portfolio'),
    (N'[portfolio].[cash_ledger_entries]', N'fk_cash_ledger_entries_balance'),
    (N'[portfolio].[cash_ledger_entries]', N'fk_cash_ledger_entries_fill'),
    (N'[portfolio].[cash_ledger_entries]', N'fk_cash_ledger_entries_order'),
    (N'[portfolio].[exposure_states]', N'fk_exposure_states_portfolio'),
    (N'[portfolio].[exposure_ledger_entries]', N'fk_exposure_ledger_entries_portfolio'),
    (N'[portfolio].[exposure_ledger_entries]', N'fk_exposure_ledger_entries_state'),
    (N'[portfolio].[exposure_ledger_entries]', N'fk_exposure_ledger_entries_position'),
    (N'[portfolio].[exposure_ledger_entries]', N'fk_exposure_ledger_entries_fill'),
    (N'[portfolio].[valuation_marks]', N'fk_valuation_marks_instrument'),
    (N'[portfolio].[valuation_marks]', N'fk_valuation_marks_candle'),
    (N'[portfolio].[position_valuations]', N'fk_position_valuations_position'),
    (N'[portfolio].[position_valuations]', N'fk_position_valuations_mark'),
    (N'[portfolio].[pnl_snapshots]', N'fk_pnl_snapshots_portfolio'),
    (N'[portfolio].[pnl_snapshot_positions]', N'fk_pnl_snapshot_positions_snapshot'),
    (N'[portfolio].[pnl_snapshot_positions]', N'fk_pnl_snapshot_positions_position'),
    (N'[portfolio].[pnl_snapshot_positions]', N'fk_pnl_snapshot_positions_valuation'),
    (N'[portfolio].[broker_position_observations]', N'fk_broker_position_observations_run'),
    (N'[portfolio].[broker_position_observations]', N'fk_broker_position_observations_observation'),
    (N'[portfolio].[broker_position_observations]', N'fk_broker_position_observations_account'),
    (N'[portfolio].[broker_position_observations]', N'fk_broker_position_observations_instrument'),
    (N'[portfolio].[position_reconciliation_states]', N'fk_position_reconciliation_states_portfolio'),
    (N'[portfolio].[position_reconciliation_states]', N'fk_position_reconciliation_states_position'),
    (N'[portfolio].[position_reconciliation_states]', N'fk_position_reconciliation_states_account'),
    (N'[portfolio].[position_reconciliation_states]', N'fk_position_reconciliation_states_instrument'),
    (N'[portfolio].[position_reconciliation_states]', N'fk_position_reconciliation_states_run'),
    (N'[portfolio].[position_reconciliation_states]', N'fk_position_reconciliation_states_observation'),
    (N'[portfolio].[position_reconciliation_events]', N'fk_position_reconciliation_events_state'),
    (N'[portfolio].[position_reconciliation_events]', N'fk_position_reconciliation_events_run'),
    (N'[portfolio].[position_reconciliation_events]', N'fk_position_reconciliation_events_observation'),
    (N'[portfolio].[position_reconciliation_events]', N'fk_position_reconciliation_events_position_event');

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

    RAISERROR('V0008 foreign-key verification failed.', 16, 1);
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
    (N'[portfolio].[portfolios]', N'ix_portfolios_status', 0, 0),
    (N'[portfolio].[positions]', N'ix_positions_open', 0, 0),
    (N'[portfolio].[positions]', N'ix_positions_instrument', 0, 0),
    (N'[portfolio].[position_events]', N'ux_position_events_fill', 1, 1),
    (N'[portfolio].[position_events]', N'ix_position_events_latest', 0, 0),
    (N'[portfolio].[position_events]', N'ix_position_events_correlation', 0, 0),
    (N'[portfolio].[position_lots]', N'ix_position_lots_open', 0, 0),
    (N'[portfolio].[position_lot_closures]', N'ix_position_lot_closures_fill', 0, 0),
    (N'[portfolio].[realized_pnl_entries]', N'ix_realized_pnl_entries_portfolio_date', 0, 0),
    (N'[portfolio].[cash_ledger_entries]', N'ux_cash_ledger_entries_fill_type', 1, 1),
    (N'[portfolio].[cash_ledger_entries]', N'ix_cash_ledger_entries_portfolio_time', 0, 0),
    (N'[portfolio].[exposure_ledger_entries]', N'ix_exposure_ledger_entries_scope', 0, 0),
    (N'[portfolio].[valuation_marks]', N'ix_valuation_marks_latest', 0, 0),
    (N'[portfolio].[position_valuations]', N'ix_position_valuations_latest', 0, 0),
    (N'[portfolio].[pnl_snapshots]', N'ix_pnl_snapshots_latest', 0, 0),
    (N'[portfolio].[broker_position_observations]', N'ix_broker_position_observations_latest', 0, 0),
    (N'[portfolio].[position_reconciliation_states]', N'ix_position_reconciliation_states_open', 0, 0),
    (N'[portfolio].[position_reconciliation_events]', N'ix_position_reconciliation_events_latest', 0, 0);

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

    RAISERROR('V0008 index verification failed.', 16, 1);
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
    (N'[portfolio].[portfolios]', N'ck_portfolios_environment'),
    (N'[portfolio].[portfolios]', N'ck_portfolios_accounting_method'),
    (N'[portfolio].[portfolios]', N'ck_portfolios_status'),
    (N'[portfolio].[portfolios]', N'ck_portfolios_exit_safety'),
    (N'[portfolio].[positions]', N'ck_positions_product_type'),
    (N'[portfolio].[positions]', N'ck_positions_values'),
    (N'[portfolio].[positions]', N'ck_positions_side_quantity'),
    (N'[portfolio].[positions]', N'ck_positions_status_state'),
    (N'[portfolio].[position_events]', N'ck_position_events_type'),
    (N'[portfolio].[position_events]', N'ck_position_events_source'),
    (N'[portfolio].[position_events]', N'ck_position_events_quantities'),
    (N'[portfolio].[position_events]', N'ck_position_events_source_reference'),
    (N'[portfolio].[position_lots]', N'ck_position_lots_values'),
    (N'[portfolio].[position_lots]', N'ck_position_lots_status_state'),
    (N'[portfolio].[position_lot_closures]', N'ck_position_lot_closures_values'),
    (N'[portfolio].[position_lot_closures]', N'ck_position_lot_closures_matching_method'),
    (N'[portfolio].[realized_pnl_entries]', N'ck_realized_pnl_entries_costs'),
    (N'[portfolio].[cash_balances]', N'ck_cash_balances_values'),
    (N'[portfolio].[cash_balances]', N'ck_cash_balances_identity'),
    (N'[portfolio].[cash_ledger_entries]', N'ck_cash_ledger_entries_type'),
    (N'[portfolio].[cash_ledger_entries]', N'ck_cash_ledger_entries_nonzero'),
    (N'[portfolio].[cash_ledger_entries]', N'ck_cash_ledger_entries_time'),
    (N'[portfolio].[exposure_states]', N'ck_exposure_states_scope'),
    (N'[portfolio].[exposure_states]', N'ck_exposure_states_values'),
    (N'[portfolio].[exposure_ledger_entries]', N'ck_exposure_ledger_entries_source'),
    (N'[portfolio].[exposure_ledger_entries]', N'ck_exposure_ledger_entries_scope'),
    (N'[portfolio].[exposure_ledger_entries]', N'ck_exposure_ledger_entries_values'),
    (N'[portfolio].[valuation_marks]', N'ck_valuation_marks_source'),
    (N'[portfolio].[valuation_marks]', N'ck_valuation_marks_quality'),
    (N'[portfolio].[valuation_marks]', N'ck_valuation_marks_eligibility'),
    (N'[portfolio].[position_valuations]', N'ck_position_valuations_values'),
    (N'[portfolio].[pnl_snapshots]', N'ck_pnl_snapshots_values'),
    (N'[portfolio].[pnl_snapshots]', N'ck_pnl_snapshots_time'),
    (N'[portfolio].[pnl_snapshots]', N'ck_pnl_snapshots_json'),
    (N'[portfolio].[pnl_snapshot_positions]', N'ck_pnl_snapshot_positions_costs'),
    (N'[portfolio].[broker_position_observations]', N'ck_broker_position_observations_product'),
    (N'[portfolio].[broker_position_observations]', N'ck_broker_position_observations_values'),
    (N'[portfolio].[broker_position_observations]', N'ck_broker_position_observations_time'),
    (N'[portfolio].[position_reconciliation_states]', N'ck_position_reconciliation_states_values'),
    (N'[portfolio].[position_reconciliation_states]', N'ck_position_reconciliation_states_status'),
    (N'[portfolio].[position_reconciliation_states]', N'ck_position_reconciliation_states_exit_safety'),
    (N'[portfolio].[position_reconciliation_states]', N'ck_position_reconciliation_states_lifecycle'),
    (N'[portfolio].[position_reconciliation_events]', N'ck_position_reconciliation_events_type'),
    (N'[portfolio].[position_reconciliation_events]', N'ck_position_reconciliation_events_approval'),
    (N'[portfolio].[position_reconciliation_events]', N'ck_position_reconciliation_events_adjustment');

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

    RAISERROR('V0008 check-constraint verification failed.', 16, 1);
    RETURN;
END;

DECLARE @rowversion_tables TABLE
(
    [table_name] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @rowversion_tables ([table_name])
VALUES
    (N'[portfolio].[portfolios]'),
    (N'[portfolio].[positions]'),
    (N'[portfolio].[position_lots]'),
    (N'[portfolio].[cash_balances]'),
    (N'[portfolio].[exposure_states]'),
    (N'[portfolio].[position_reconciliation_states]');

IF EXISTS
(
    SELECT 1
    FROM @rowversion_tables AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.columns AS columns
        INNER JOIN sys.types AS types
            ON columns.[user_type_id] = types.[user_type_id]
        WHERE columns.[object_id] = OBJECT_ID(expected.[table_name])
          AND columns.[name] = N'row_version'
          AND types.[name] = N'timestamp'
    )
)
BEGIN
    SELECT expected.[table_name] AS [missing_rowversion_projection]
    FROM @rowversion_tables AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.columns AS columns
        INNER JOIN sys.types AS types
            ON columns.[user_type_id] = types.[user_type_id]
        WHERE columns.[object_id] = OBJECT_ID(expected.[table_name])
          AND columns.[name] = N'row_version'
          AND types.[name] = N'timestamp'
    );

    RAISERROR('V0008 rowversion projection verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[portfolio].[positions]')
      AND columns.[name] IN
      (
          N'quantity', N'average_open_price', N'cost_basis_amount',
          N'market_value_amount', N'realized_pnl_amount', N'unrealized_pnl_amount',
          N'accrued_fees_amount', N'accrued_taxes_amount', N'protective_exit_quantity'
      )
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 9
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
    RAISERROR('V0008 position precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[portfolio].[cash_balances]')
      AND columns.[name] IN
      (
          N'settled_amount', N'unsettled_receivable_amount', N'unsettled_payable_amount',
          N'reserved_amount', N'total_balance_amount', N'available_amount'
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
    RAISERROR('V0008 cash precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[portfolio].[pnl_snapshots]')
      AND columns.[name] IN
      (
          N'realized_pnl_amount', N'unrealized_pnl_amount', N'gross_pnl_amount',
          N'fees_amount', N'taxes_amount', N'net_pnl_amount',
          N'gross_exposure_amount', N'net_exposure_amount',
          N'cash_balance_amount', N'net_liquidation_value_amount'
      )
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 10
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
    RAISERROR('V0008 P&L precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [operations].[database_metadata]
    WHERE [database_metadata_id] = 1
      AND [schema_baseline_version] = 'V0008'
)
BEGIN
    RAISERROR('Database metadata was not advanced to V0008.', 16, 1);
    RETURN;
END;

SELECT
    'PASS' AS [verification_status],
    'V0008' AS [migration_version],
    DB_NAME() AS [database_name],
    (SELECT COUNT_BIG(*) FROM @expected_tables) AS [verified_table_count],
    (SELECT COUNT_BIG(*) FROM @expected_foreign_keys) AS [verified_foreign_key_count],
    (SELECT COUNT_BIG(*) FROM @expected_indexes) AS [verified_index_count],
    (SELECT COUNT_BIG(*) FROM @expected_checks) AS [verified_check_count],
    (SELECT COUNT_BIG(*) FROM @rowversion_tables) AS [verified_projection_count];
