/*
Migration: V0008__create_portfolio_and_pnl_tables.sql
Purpose:
  Create canonical portfolio identities, mutable position/cash/exposure projections,
  append-only position, lot, cash, exposure and realized-P&L ledgers, immutable
  valuation/P&L snapshots, and broker-position reconciliation evidence and state.
Dependencies:
  V0001__create_schemas_and_migration_metadata.sql
  V0002__create_reference_tables.sql
  V0003__create_market_data_tables.sql
  V0007__create_execution_and_reconciliation_tables.sql
Expected runtime impact:
  Additive DDL only. No positions, fills or cash balances are backfilled.
Locking considerations:
  Schema modification locks are acquired while tables, constraints and indexes are created.
Backward-compatibility window:
  Fully additive.
Data migration requirements:
  None.
Verification script:
  database/verification/V0008__verify_portfolio_and_pnl_tables.sql
Recovery plan:
  Roll forward with a later migration. Destructive rollback is limited to disposable local databases.
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

BEGIN TRY
    BEGIN TRANSACTION;

    IF SCHEMA_ID(N'portfolio') IS NULL
        THROW 58001, 'V0001 is required: schema portfolio does not exist.', 1;

    IF OBJECT_ID(N'[reference].[instruments]', N'U') IS NULL
        THROW 58002, 'V0002 is required: reference.instruments does not exist.', 1;

    IF OBJECT_ID(N'[market].[candles]', N'U') IS NULL
        THROW 58003, 'V0003 is required: market.candles does not exist.', 1;

    IF OBJECT_ID(N'[execution].[fills]', N'U') IS NULL
        THROW 58004, 'V0007 is required: execution.fills does not exist.', 1;

    IF OBJECT_ID(N'[execution].[reconciliation_runs]', N'U') IS NULL
        THROW 58005, 'V0007 is required: execution.reconciliation_runs does not exist.', 1;

    IF OBJECT_ID(N'[broker].[broker_accounts]', N'U') IS NULL
        THROW 58006, 'V0007 is required: broker.broker_accounts does not exist.', 1;

    IF OBJECT_ID(N'[portfolio].[portfolios]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[portfolios]
        (
            [portfolio_id] bigint IDENTITY(1,1) NOT NULL,
            [portfolio_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_portfolios_uid] DEFAULT NEWSEQUENTIALID(),
            [portfolio_code] varchar(100) NOT NULL,
            [portfolio_name] nvarchar(200) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [broker_account_id] bigint NOT NULL,
            [strategy_code] varchar(100) NOT NULL,
            [base_currency_code] char(3) NOT NULL,
            [accounting_method] varchar(30) NOT NULL,
            [status] varchar(30) NOT NULL,
            [allows_new_exposure] bit NOT NULL
                CONSTRAINT [df_portfolios_allows_new_exposure] DEFAULT (0),
            [allows_risk_reducing_exits] bit NOT NULL
                CONSTRAINT [df_portfolios_allows_exits] DEFAULT (1),
            [effective_from_utc] datetime2(7) NOT NULL,
            [effective_to_utc] datetime2(7) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolios_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolios_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_portfolios]
                PRIMARY KEY CLUSTERED ([portfolio_id]),
            CONSTRAINT [uq_portfolios_uid]
                UNIQUE ([portfolio_uid]),
            CONSTRAINT [uq_portfolios_code]
                UNIQUE ([environment], [broker_account_id], [portfolio_code]),
            CONSTRAINT [uq_portfolios_strategy]
                UNIQUE ([environment], [broker_account_id], [strategy_code]),
            CONSTRAINT [fk_portfolios_broker_account]
                FOREIGN KEY ([broker_account_id])
                REFERENCES [broker].[broker_accounts] ([broker_account_id]),
            CONSTRAINT [ck_portfolios_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_portfolios_currency]
                CHECK (LEN(RTRIM([base_currency_code])) = 3),
            CONSTRAINT [ck_portfolios_accounting_method]
                CHECK ([accounting_method] IN ('FIFO', 'LIFO', 'WEIGHTED_AVERAGE')),
            CONSTRAINT [ck_portfolios_status]
                CHECK ([status] IN ('ACTIVE', 'RESTRICTED', 'CLOSE_ONLY', 'SUSPENDED', 'CLOSED')),
            CONSTRAINT [ck_portfolios_exit_safety]
                CHECK ([allows_new_exposure] = 0 OR [allows_risk_reducing_exits] = 1),
            CONSTRAINT [ck_portfolios_effective_window]
                CHECK ([effective_to_utc] IS NULL OR [effective_to_utc] > [effective_from_utc])
        );

        CREATE INDEX [ix_portfolios_status]
            ON [portfolio].[portfolios]
            ([environment], [status], [broker_account_id], [strategy_code]);
    END;

    IF OBJECT_ID(N'[portfolio].[positions]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[positions]
        (
            [position_id] bigint IDENTITY(1,1) NOT NULL,
            [position_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_positions_uid] DEFAULT NEWSEQUENTIALID(),
            [portfolio_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [product_type] varchar(30) NOT NULL,
            [position_side] varchar(10) NOT NULL,
            [quantity] decimal(19,6) NOT NULL,
            [average_open_price] decimal(19,6) NULL,
            [cost_basis_amount] decimal(19,6) NOT NULL,
            [market_value_amount] decimal(19,6) NOT NULL,
            [realized_pnl_amount] decimal(19,6) NOT NULL,
            [unrealized_pnl_amount] decimal(19,6) NOT NULL,
            [accrued_fees_amount] decimal(19,6) NOT NULL,
            [accrued_taxes_amount] decimal(19,6) NOT NULL,
            [protective_exit_quantity] decimal(19,6) NOT NULL,
            [status] varchar(30) NOT NULL,
            [current_position_version] int NOT NULL,
            [last_event_sequence] int NOT NULL,
            [opened_at_utc] datetime2(7) NULL,
            [last_fill_at_utc] datetime2(7) NULL,
            [closed_at_utc] datetime2(7) NULL,
            [last_valued_at_utc] datetime2(7) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_positions_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_positions_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_positions]
                PRIMARY KEY CLUSTERED ([position_id]),
            CONSTRAINT [uq_positions_uid]
                UNIQUE ([position_uid]),
            CONSTRAINT [uq_positions_id_instrument]
                UNIQUE ([position_id], [instrument_id]),
            CONSTRAINT [uq_positions_scope]
                UNIQUE ([portfolio_id], [instrument_id], [product_type]),
            CONSTRAINT [fk_positions_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [fk_positions_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [ck_positions_product_type]
                CHECK ([product_type] IN ('CASH', 'DELIVERY', 'INTRADAY', 'FUTURE', 'OPTION')),
            CONSTRAINT [ck_positions_side]
                CHECK ([position_side] IN ('LONG', 'SHORT', 'FLAT')),
            CONSTRAINT [ck_positions_values]
                CHECK
                (
                    [quantity] >= 0
                    AND ([average_open_price] IS NULL OR [average_open_price] > 0)
                    AND [cost_basis_amount] >= 0
                    AND [market_value_amount] >= 0
                    AND [accrued_fees_amount] >= 0
                    AND [accrued_taxes_amount] >= 0
                    AND [protective_exit_quantity] >= 0
                    AND [protective_exit_quantity] <= [quantity]
                    AND [current_position_version] >= 0
                    AND [last_event_sequence] >= 0
                ),
            CONSTRAINT [ck_positions_side_quantity]
                CHECK
                (
                    ([quantity] = 0 AND [position_side] = 'FLAT' AND [average_open_price] IS NULL)
                    OR
                    ([quantity] > 0 AND [position_side] IN ('LONG', 'SHORT')
                        AND [average_open_price] IS NOT NULL)
                ),
            CONSTRAINT [ck_positions_status]
                CHECK ([status] IN ('OPEN', 'CLOSED', 'RECONCILIATION_REQUIRED')),
            CONSTRAINT [ck_positions_status_state]
                CHECK
                (
                    ([status] = 'OPEN' AND [quantity] > 0 AND [closed_at_utc] IS NULL)
                    OR
                    ([status] = 'CLOSED' AND [quantity] = 0 AND [closed_at_utc] IS NOT NULL)
                    OR
                    ([status] = 'RECONCILIATION_REQUIRED')
                ),
            CONSTRAINT [ck_positions_open_time]
                CHECK ([quantity] = 0 OR [opened_at_utc] IS NOT NULL)
        );

        CREATE INDEX [ix_positions_open]
            ON [portfolio].[positions]
            ([portfolio_id], [status], [instrument_id])
            INCLUDE
            (
                [product_type], [position_side], [quantity], [average_open_price],
                [market_value_amount], [realized_pnl_amount], [unrealized_pnl_amount],
                [protective_exit_quantity], [current_position_version]
            );

        CREATE INDEX [ix_positions_instrument]
            ON [portfolio].[positions]
            ([instrument_id], [status], [updated_at_utc] DESC);
    END;

    IF OBJECT_ID(N'[portfolio].[position_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[position_events]
        (
            [position_event_id] bigint IDENTITY(1,1) NOT NULL,
            [position_event_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_position_events_uid] DEFAULT NEWSEQUENTIALID(),
            [position_id] bigint NOT NULL,
            [fill_id] bigint NULL,
            [order_id] bigint NULL,
            [reconciliation_run_id] bigint NULL,
            [event_sequence] int NOT NULL,
            [resulting_position_version] int NOT NULL,
            [event_type] varchar(40) NOT NULL,
            [source_type] varchar(30) NOT NULL,
            [side_before] varchar(10) NOT NULL,
            [side_after] varchar(10) NOT NULL,
            [quantity_before] decimal(19,6) NOT NULL,
            [quantity_delta] decimal(19,6) NOT NULL,
            [quantity_after] decimal(19,6) NOT NULL,
            [average_price_before] decimal(19,6) NULL,
            [average_price_after] decimal(19,6) NULL,
            [cost_basis_before] decimal(19,6) NOT NULL,
            [cost_basis_after] decimal(19,6) NOT NULL,
            [realized_pnl_delta] decimal(19,6) NOT NULL,
            [fees_delta] decimal(19,6) NOT NULL,
            [taxes_delta] decimal(19,6) NOT NULL,
            [event_at_utc] datetime2(7) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_position_events_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_position_events]
                PRIMARY KEY CLUSTERED ([position_event_id]),
            CONSTRAINT [uq_position_events_uid]
                UNIQUE ([position_event_uid]),
            CONSTRAINT [uq_position_events_sequence]
                UNIQUE ([position_id], [event_sequence]),
            CONSTRAINT [uq_position_events_version]
                UNIQUE ([position_id], [resulting_position_version]),
            CONSTRAINT [fk_position_events_position]
                FOREIGN KEY ([position_id])
                REFERENCES [portfolio].[positions] ([position_id]),
            CONSTRAINT [fk_position_events_fill]
                FOREIGN KEY ([fill_id])
                REFERENCES [execution].[fills] ([fill_id]),
            CONSTRAINT [fk_position_events_order]
                FOREIGN KEY ([order_id])
                REFERENCES [execution].[orders] ([order_id]),
            CONSTRAINT [fk_position_events_reconciliation_run]
                FOREIGN KEY ([reconciliation_run_id])
                REFERENCES [execution].[reconciliation_runs] ([reconciliation_run_id]),
            CONSTRAINT [ck_position_events_sequence]
                CHECK ([event_sequence] >= 1 AND [resulting_position_version] >= 1),
            CONSTRAINT [ck_position_events_type]
                CHECK
                (
                    [event_type] IN
                    ('OPENED', 'INCREASED', 'REDUCED', 'CLOSED', 'REVERSED',
                     'ADJUSTED', 'RECONCILIATION_REQUIRED', 'RECONCILED')
                ),
            CONSTRAINT [ck_position_events_source]
                CHECK ([source_type] IN ('FILL', 'RECONCILIATION', 'CORPORATE_ACTION', 'MANUAL')),
            CONSTRAINT [ck_position_events_sides]
                CHECK
                (
                    [side_before] IN ('LONG', 'SHORT', 'FLAT')
                    AND [side_after] IN ('LONG', 'SHORT', 'FLAT')
                ),
            CONSTRAINT [ck_position_events_quantities]
                CHECK
                (
                    [quantity_before] >= 0
                    AND [quantity_after] >= 0
                    AND [quantity_after] = [quantity_before] + [quantity_delta]
                    AND
                    (([quantity_before] = 0 AND [side_before] = 'FLAT' AND [average_price_before] IS NULL)
                        OR ([quantity_before] > 0 AND [side_before] IN ('LONG', 'SHORT')
                            AND [average_price_before] > 0))
                    AND
                    (([quantity_after] = 0 AND [side_after] = 'FLAT' AND [average_price_after] IS NULL)
                        OR ([quantity_after] > 0 AND [side_after] IN ('LONG', 'SHORT')
                            AND [average_price_after] > 0))
                ),
            CONSTRAINT [ck_position_events_amounts]
                CHECK
                (
                    [cost_basis_before] >= 0
                    AND [cost_basis_after] >= 0
                    AND [fees_delta] >= 0
                    AND [taxes_delta] >= 0
                ),
            CONSTRAINT [ck_position_events_source_reference]
                CHECK
                (
                    ([source_type] = 'FILL' AND [fill_id] IS NOT NULL)
                    OR
                    ([source_type] = 'RECONCILIATION' AND [reconciliation_run_id] IS NOT NULL)
                    OR
                    ([source_type] IN ('CORPORATE_ACTION', 'MANUAL'))
                ),
            CONSTRAINT [ck_position_events_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE UNIQUE INDEX [ux_position_events_fill]
            ON [portfolio].[position_events] ([fill_id])
            WHERE [fill_id] IS NOT NULL;

        CREATE INDEX [ix_position_events_latest]
            ON [portfolio].[position_events]
            ([position_id], [event_sequence] DESC)
            INCLUDE
            (
                [event_type], [quantity_after], [side_after],
                [resulting_position_version], [event_at_utc]
            );

        CREATE INDEX [ix_position_events_correlation]
            ON [portfolio].[position_events] ([correlation_id], [event_at_utc]);
    END;

    IF OBJECT_ID(N'[portfolio].[position_lots]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[position_lots]
        (
            [position_lot_id] bigint IDENTITY(1,1) NOT NULL,
            [position_lot_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_position_lots_uid] DEFAULT NEWSEQUENTIALID(),
            [position_id] bigint NOT NULL,
            [opening_fill_id] bigint NOT NULL,
            [lot_sequence] int NOT NULL,
            [lot_side] varchar(10) NOT NULL,
            [opened_quantity] decimal(19,6) NOT NULL,
            [remaining_quantity] decimal(19,6) NOT NULL,
            [open_price] decimal(19,6) NOT NULL,
            [open_gross_amount] decimal(19,6) NOT NULL,
            [allocated_open_fees_amount] decimal(19,6) NOT NULL,
            [allocated_open_taxes_amount] decimal(19,6) NOT NULL,
            [opened_at_utc] datetime2(7) NOT NULL,
            [status] varchar(20) NOT NULL,
            [closed_at_utc] datetime2(7) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_position_lots_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_position_lots_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_position_lots]
                PRIMARY KEY CLUSTERED ([position_lot_id]),
            CONSTRAINT [uq_position_lots_uid]
                UNIQUE ([position_lot_uid]),
            CONSTRAINT [uq_position_lots_sequence]
                UNIQUE ([position_id], [lot_sequence]),
            CONSTRAINT [uq_position_lots_opening_fill]
                UNIQUE ([opening_fill_id]),
            CONSTRAINT [fk_position_lots_position]
                FOREIGN KEY ([position_id])
                REFERENCES [portfolio].[positions] ([position_id]),
            CONSTRAINT [fk_position_lots_opening_fill]
                FOREIGN KEY ([opening_fill_id])
                REFERENCES [execution].[fills] ([fill_id]),
            CONSTRAINT [ck_position_lots_sequence]
                CHECK ([lot_sequence] >= 1),
            CONSTRAINT [ck_position_lots_side]
                CHECK ([lot_side] IN ('LONG', 'SHORT')),
            CONSTRAINT [ck_position_lots_values]
                CHECK
                (
                    [opened_quantity] > 0
                    AND [remaining_quantity] >= 0
                    AND [remaining_quantity] <= [opened_quantity]
                    AND [open_price] > 0
                    AND [open_gross_amount] >= 0
                    AND [allocated_open_fees_amount] >= 0
                    AND [allocated_open_taxes_amount] >= 0
                ),
            CONSTRAINT [ck_position_lots_status]
                CHECK ([status] IN ('OPEN', 'CLOSED')),
            CONSTRAINT [ck_position_lots_status_state]
                CHECK
                (
                    ([status] = 'OPEN' AND [remaining_quantity] > 0 AND [closed_at_utc] IS NULL)
                    OR
                    ([status] = 'CLOSED' AND [remaining_quantity] = 0 AND [closed_at_utc] IS NOT NULL)
                )
        );

        CREATE INDEX [ix_position_lots_open]
            ON [portfolio].[position_lots]
            ([position_id], [status], [lot_sequence])
            INCLUDE ([lot_side], [remaining_quantity], [open_price], [opened_at_utc]);
    END;

    IF OBJECT_ID(N'[portfolio].[position_lot_closures]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[position_lot_closures]
        (
            [position_lot_closure_id] bigint IDENTITY(1,1) NOT NULL,
            [position_lot_closure_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_position_lot_closures_uid] DEFAULT NEWSEQUENTIALID(),
            [position_lot_id] bigint NOT NULL,
            [closing_fill_id] bigint NOT NULL,
            [position_event_id] bigint NOT NULL,
            [closure_sequence] int NOT NULL,
            [matched_quantity] decimal(19,6) NOT NULL,
            [open_price] decimal(19,6) NOT NULL,
            [close_price] decimal(19,6) NOT NULL,
            [gross_realized_pnl_amount] decimal(19,6) NOT NULL,
            [allocated_open_fees_amount] decimal(19,6) NOT NULL,
            [allocated_close_fees_amount] decimal(19,6) NOT NULL,
            [allocated_open_taxes_amount] decimal(19,6) NOT NULL,
            [allocated_close_taxes_amount] decimal(19,6) NOT NULL,
            [net_realized_pnl_amount] decimal(19,6) NOT NULL,
            [matching_method] varchar(30) NOT NULL,
            [closed_at_utc] datetime2(7) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_position_lot_closures_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_position_lot_closures]
                PRIMARY KEY CLUSTERED ([position_lot_closure_id]),
            CONSTRAINT [uq_position_lot_closures_uid]
                UNIQUE ([position_lot_closure_uid]),
            CONSTRAINT [uq_position_lot_closures_match]
                UNIQUE ([position_lot_id], [closing_fill_id]),
            CONSTRAINT [uq_position_lot_closures_sequence]
                UNIQUE ([closing_fill_id], [closure_sequence]),
            CONSTRAINT [fk_position_lot_closures_lot]
                FOREIGN KEY ([position_lot_id])
                REFERENCES [portfolio].[position_lots] ([position_lot_id]),
            CONSTRAINT [fk_position_lot_closures_fill]
                FOREIGN KEY ([closing_fill_id])
                REFERENCES [execution].[fills] ([fill_id]),
            CONSTRAINT [fk_position_lot_closures_event]
                FOREIGN KEY ([position_event_id])
                REFERENCES [portfolio].[position_events] ([position_event_id]),
            CONSTRAINT [ck_position_lot_closures_sequence]
                CHECK ([closure_sequence] >= 1),
            CONSTRAINT [ck_position_lot_closures_values]
                CHECK
                (
                    [matched_quantity] > 0
                    AND [open_price] > 0
                    AND [close_price] > 0
                    AND [allocated_open_fees_amount] >= 0
                    AND [allocated_close_fees_amount] >= 0
                    AND [allocated_open_taxes_amount] >= 0
                    AND [allocated_close_taxes_amount] >= 0
                ),
            CONSTRAINT [ck_position_lot_closures_matching_method]
                CHECK ([matching_method] IN ('FIFO', 'LIFO', 'WEIGHTED_AVERAGE', 'RECONCILIATION'))
        );

        CREATE INDEX [ix_position_lot_closures_fill]
            ON [portfolio].[position_lot_closures]
            ([closing_fill_id], [closure_sequence])
            INCLUDE ([position_lot_id], [matched_quantity], [net_realized_pnl_amount]);
    END;

    IF OBJECT_ID(N'[portfolio].[realized_pnl_entries]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[realized_pnl_entries]
        (
            [realized_pnl_entry_id] bigint IDENTITY(1,1) NOT NULL,
            [realized_pnl_entry_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_realized_pnl_entries_uid] DEFAULT NEWSEQUENTIALID(),
            [portfolio_id] bigint NOT NULL,
            [position_id] bigint NOT NULL,
            [position_lot_closure_id] bigint NOT NULL,
            [closing_fill_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [trade_date] date NOT NULL,
            [currency_code] char(3) NOT NULL,
            [gross_realized_pnl_amount] decimal(19,6) NOT NULL,
            [fees_amount] decimal(19,6) NOT NULL,
            [taxes_amount] decimal(19,6) NOT NULL,
            [net_realized_pnl_amount] decimal(19,6) NOT NULL,
            [recognized_at_utc] datetime2(7) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_realized_pnl_entries_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_realized_pnl_entries]
                PRIMARY KEY CLUSTERED ([realized_pnl_entry_id]),
            CONSTRAINT [uq_realized_pnl_entries_uid]
                UNIQUE ([realized_pnl_entry_uid]),
            CONSTRAINT [uq_realized_pnl_entries_closure]
                UNIQUE ([position_lot_closure_id]),
            CONSTRAINT [fk_realized_pnl_entries_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [fk_realized_pnl_entries_position]
                FOREIGN KEY ([position_id], [instrument_id])
                REFERENCES [portfolio].[positions] ([position_id], [instrument_id]),
            CONSTRAINT [fk_realized_pnl_entries_closure]
                FOREIGN KEY ([position_lot_closure_id])
                REFERENCES [portfolio].[position_lot_closures] ([position_lot_closure_id]),
            CONSTRAINT [fk_realized_pnl_entries_fill]
                FOREIGN KEY ([closing_fill_id])
                REFERENCES [execution].[fills] ([fill_id]),
            CONSTRAINT [ck_realized_pnl_entries_currency]
                CHECK (LEN(RTRIM([currency_code])) = 3),
            CONSTRAINT [ck_realized_pnl_entries_costs]
                CHECK ([fees_amount] >= 0 AND [taxes_amount] >= 0)
        );

        CREATE INDEX [ix_realized_pnl_entries_portfolio_date]
            ON [portfolio].[realized_pnl_entries]
            ([portfolio_id], [trade_date], [recognized_at_utc])
            INCLUDE
            (
                [position_id], [instrument_id], [gross_realized_pnl_amount],
                [fees_amount], [taxes_amount], [net_realized_pnl_amount]
            );
    END;

    IF OBJECT_ID(N'[portfolio].[cash_balances]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[cash_balances]
        (
            [cash_balance_id] bigint IDENTITY(1,1) NOT NULL,
            [portfolio_id] bigint NOT NULL,
            [currency_code] char(3) NOT NULL,
            [settled_amount] decimal(19,6) NOT NULL,
            [unsettled_receivable_amount] decimal(19,6) NOT NULL,
            [unsettled_payable_amount] decimal(19,6) NOT NULL,
            [reserved_amount] decimal(19,6) NOT NULL,
            [total_balance_amount] decimal(19,6) NOT NULL,
            [available_amount] decimal(19,6) NOT NULL,
            [current_balance_version] int NOT NULL,
            [last_ledger_sequence] bigint NOT NULL,
            [as_of_utc] datetime2(7) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_cash_balances_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_cash_balances_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_cash_balances]
                PRIMARY KEY CLUSTERED ([cash_balance_id]),
            CONSTRAINT [uq_cash_balances_scope]
                UNIQUE ([portfolio_id], [currency_code]),
            CONSTRAINT [fk_cash_balances_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [ck_cash_balances_currency]
                CHECK (LEN(RTRIM([currency_code])) = 3),
            CONSTRAINT [ck_cash_balances_values]
                CHECK
                (
                    [unsettled_receivable_amount] >= 0
                    AND [unsettled_payable_amount] >= 0
                    AND [reserved_amount] >= 0
                    AND [current_balance_version] >= 0
                    AND [last_ledger_sequence] >= 0
                ),
            CONSTRAINT [ck_cash_balances_identity]
                CHECK
                (
                    [total_balance_amount] =
                        [settled_amount] + [unsettled_receivable_amount] - [unsettled_payable_amount]
                    AND [available_amount] = [total_balance_amount] - [reserved_amount]
                )
        );
    END;

    IF OBJECT_ID(N'[portfolio].[cash_ledger_entries]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[cash_ledger_entries]
        (
            [cash_ledger_entry_id] bigint IDENTITY(1,1) NOT NULL,
            [cash_ledger_entry_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_cash_ledger_entries_uid] DEFAULT NEWSEQUENTIALID(),
            [portfolio_id] bigint NOT NULL,
            [cash_balance_id] bigint NOT NULL,
            [fill_id] bigint NULL,
            [order_id] bigint NULL,
            [ledger_sequence] bigint NOT NULL,
            [idempotency_key] varchar(200) NOT NULL,
            [entry_type] varchar(40) NOT NULL,
            [currency_code] char(3) NOT NULL,
            [settled_delta_amount] decimal(19,6) NOT NULL,
            [unsettled_receivable_delta_amount] decimal(19,6) NOT NULL,
            [unsettled_payable_delta_amount] decimal(19,6) NOT NULL,
            [reserved_delta_amount] decimal(19,6) NOT NULL,
            [effective_at_utc] datetime2(7) NOT NULL,
            [posted_at_utc] datetime2(7) NOT NULL,
            [settlement_date] date NULL,
            [description] nvarchar(1000) NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_cash_ledger_entries_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_cash_ledger_entries]
                PRIMARY KEY CLUSTERED ([cash_ledger_entry_id]),
            CONSTRAINT [uq_cash_ledger_entries_uid]
                UNIQUE ([cash_ledger_entry_uid]),
            CONSTRAINT [uq_cash_ledger_entries_sequence]
                UNIQUE ([portfolio_id], [currency_code], [ledger_sequence]),
            CONSTRAINT [uq_cash_ledger_entries_idempotency]
                UNIQUE ([portfolio_id], [idempotency_key]),
            CONSTRAINT [fk_cash_ledger_entries_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [fk_cash_ledger_entries_balance]
                FOREIGN KEY ([cash_balance_id])
                REFERENCES [portfolio].[cash_balances] ([cash_balance_id]),
            CONSTRAINT [fk_cash_ledger_entries_fill]
                FOREIGN KEY ([fill_id])
                REFERENCES [execution].[fills] ([fill_id]),
            CONSTRAINT [fk_cash_ledger_entries_order]
                FOREIGN KEY ([order_id])
                REFERENCES [execution].[orders] ([order_id]),
            CONSTRAINT [ck_cash_ledger_entries_sequence]
                CHECK ([ledger_sequence] >= 1),
            CONSTRAINT [ck_cash_ledger_entries_type]
                CHECK
                (
                    [entry_type] IN
                    ('DEPOSIT', 'WITHDRAWAL', 'TRADE_SETTLEMENT', 'FEE', 'TAX',
                     'DIVIDEND', 'INTEREST', 'MARGIN_BLOCK', 'MARGIN_RELEASE',
                     'CORPORATE_ACTION', 'ADJUSTMENT', 'RECONCILIATION')
                ),
            CONSTRAINT [ck_cash_ledger_entries_currency]
                CHECK (LEN(RTRIM([currency_code])) = 3),
            CONSTRAINT [ck_cash_ledger_entries_nonzero]
                CHECK
                (
                    [settled_delta_amount] <> 0
                    OR [unsettled_receivable_delta_amount] <> 0
                    OR [unsettled_payable_delta_amount] <> 0
                    OR [reserved_delta_amount] <> 0
                ),
            CONSTRAINT [ck_cash_ledger_entries_time]
                CHECK ([posted_at_utc] >= [effective_at_utc]),
            CONSTRAINT [ck_cash_ledger_entries_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE UNIQUE INDEX [ux_cash_ledger_entries_fill_type]
            ON [portfolio].[cash_ledger_entries]
            ([fill_id], [entry_type], [currency_code])
            WHERE [fill_id] IS NOT NULL;

        CREATE INDEX [ix_cash_ledger_entries_portfolio_time]
            ON [portfolio].[cash_ledger_entries]
            ([portfolio_id], [currency_code], [effective_at_utc], [ledger_sequence]);
    END;

    IF OBJECT_ID(N'[portfolio].[exposure_states]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[exposure_states]
        (
            [exposure_state_id] bigint IDENTITY(1,1) NOT NULL,
            [portfolio_id] bigint NOT NULL,
            [scope_type] varchar(30) NOT NULL,
            [scope_reference] varchar(200) NOT NULL,
            [exposure_amount] decimal(19,6) NOT NULL,
            [exposure_fraction] decimal(19,8) NULL,
            [current_exposure_version] int NOT NULL,
            [last_ledger_sequence] bigint NOT NULL,
            [as_of_utc] datetime2(7) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_exposure_states_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_exposure_states_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_exposure_states]
                PRIMARY KEY CLUSTERED ([exposure_state_id]),
            CONSTRAINT [uq_exposure_states_scope]
                UNIQUE ([portfolio_id], [scope_type], [scope_reference]),
            CONSTRAINT [fk_exposure_states_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [ck_exposure_states_scope]
                CHECK
                (
                    [scope_type] IN
                    ('GROSS', 'NET', 'INSTRUMENT', 'SECTOR', 'CORRELATION',
                     'PRODUCT_TYPE', 'STRATEGY')
                ),
            CONSTRAINT [ck_exposure_states_values]
                CHECK
                (
                    ([scope_type] = 'NET' OR [exposure_amount] >= 0)
                    AND ([scope_type] = 'NET' OR [exposure_fraction] IS NULL OR [exposure_fraction] >= 0)
                    AND [current_exposure_version] >= 0
                    AND [last_ledger_sequence] >= 0
                )
        );
    END;

    IF OBJECT_ID(N'[portfolio].[exposure_ledger_entries]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[exposure_ledger_entries]
        (
            [exposure_ledger_entry_id] bigint IDENTITY(1,1) NOT NULL,
            [exposure_ledger_entry_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_exposure_ledger_entries_uid] DEFAULT NEWSEQUENTIALID(),
            [portfolio_id] bigint NOT NULL,
            [exposure_state_id] bigint NOT NULL,
            [position_id] bigint NULL,
            [fill_id] bigint NULL,
            [ledger_sequence] bigint NOT NULL,
            [idempotency_key] varchar(200) NOT NULL,
            [source_type] varchar(30) NOT NULL,
            [scope_type] varchar(30) NOT NULL,
            [scope_reference] varchar(200) NOT NULL,
            [exposure_amount_before] decimal(19,6) NOT NULL,
            [exposure_amount_after] decimal(19,6) NOT NULL,
            [exposure_fraction_before] decimal(19,8) NULL,
            [exposure_fraction_after] decimal(19,8) NULL,
            [as_of_utc] datetime2(7) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_exposure_ledger_entries_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_exposure_ledger_entries]
                PRIMARY KEY CLUSTERED ([exposure_ledger_entry_id]),
            CONSTRAINT [uq_exposure_ledger_entries_uid]
                UNIQUE ([exposure_ledger_entry_uid]),
            CONSTRAINT [uq_exposure_ledger_entries_sequence]
                UNIQUE ([portfolio_id], [ledger_sequence]),
            CONSTRAINT [uq_exposure_ledger_entries_idempotency]
                UNIQUE ([portfolio_id], [idempotency_key]),
            CONSTRAINT [fk_exposure_ledger_entries_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [fk_exposure_ledger_entries_state]
                FOREIGN KEY ([exposure_state_id])
                REFERENCES [portfolio].[exposure_states] ([exposure_state_id]),
            CONSTRAINT [fk_exposure_ledger_entries_position]
                FOREIGN KEY ([position_id])
                REFERENCES [portfolio].[positions] ([position_id]),
            CONSTRAINT [fk_exposure_ledger_entries_fill]
                FOREIGN KEY ([fill_id])
                REFERENCES [execution].[fills] ([fill_id]),
            CONSTRAINT [ck_exposure_ledger_entries_sequence]
                CHECK ([ledger_sequence] >= 1),
            CONSTRAINT [ck_exposure_ledger_entries_source]
                CHECK ([source_type] IN ('FILL', 'VALUATION', 'CASH', 'RECONCILIATION', 'MANUAL')),
            CONSTRAINT [ck_exposure_ledger_entries_scope]
                CHECK
                (
                    [scope_type] IN
                    ('GROSS', 'NET', 'INSTRUMENT', 'SECTOR', 'CORRELATION',
                     'PRODUCT_TYPE', 'STRATEGY')
                ),
            CONSTRAINT [ck_exposure_ledger_entries_values]
                CHECK
                (
                    ([scope_type] = 'NET'
                        OR ([exposure_amount_before] >= 0 AND [exposure_amount_after] >= 0))
                    AND ([scope_type] = 'NET'
                        OR [exposure_fraction_before] IS NULL
                        OR [exposure_fraction_before] >= 0)
                    AND ([scope_type] = 'NET'
                        OR [exposure_fraction_after] IS NULL
                        OR [exposure_fraction_after] >= 0)
                ),
            CONSTRAINT [ck_exposure_ledger_entries_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE INDEX [ix_exposure_ledger_entries_scope]
            ON [portfolio].[exposure_ledger_entries]
            ([portfolio_id], [scope_type], [scope_reference], [as_of_utc], [ledger_sequence]);
    END;

    IF OBJECT_ID(N'[portfolio].[valuation_marks]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[valuation_marks]
        (
            [valuation_mark_id] bigint IDENTITY(1,1) NOT NULL,
            [valuation_mark_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_valuation_marks_uid] DEFAULT NEWSEQUENTIALID(),
            [instrument_id] bigint NOT NULL,
            [market_candle_id] bigint NULL,
            [source_type] varchar(30) NOT NULL,
            [source_reference] varchar(200) NOT NULL,
            [mark_price] decimal(19,6) NOT NULL,
            [quality_status] varchar(20) NOT NULL,
            [is_stale] bit NOT NULL,
            [age_milliseconds] bigint NOT NULL,
            [eligible_for_risk] bit NOT NULL,
            [as_of_utc] datetime2(7) NOT NULL,
            [received_at_utc] datetime2(7) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_valuation_marks_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_valuation_marks]
                PRIMARY KEY CLUSTERED ([valuation_mark_id]),
            CONSTRAINT [uq_valuation_marks_uid]
                UNIQUE ([valuation_mark_uid]),
            CONSTRAINT [uq_valuation_marks_source]
                UNIQUE ([instrument_id], [source_type], [source_reference], [as_of_utc]),
            CONSTRAINT [fk_valuation_marks_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_valuation_marks_candle]
                FOREIGN KEY ([market_candle_id])
                REFERENCES [market].[candles] ([candle_id]),
            CONSTRAINT [ck_valuation_marks_source]
                CHECK ([source_type] IN ('MARKET_CANDLE', 'BROKER', 'SETTLEMENT', 'MANUAL')),
            CONSTRAINT [ck_valuation_marks_price]
                CHECK ([mark_price] > 0),
            CONSTRAINT [ck_valuation_marks_quality]
                CHECK ([quality_status] IN ('VALID', 'DEGRADED', 'INVALID')),
            CONSTRAINT [ck_valuation_marks_age]
                CHECK ([age_milliseconds] >= 0),
            CONSTRAINT [ck_valuation_marks_eligibility]
                CHECK
                (
                    [eligible_for_risk] = 0
                    OR ([quality_status] <> 'INVALID' AND [is_stale] = 0)
                ),
            CONSTRAINT [ck_valuation_marks_time]
                CHECK ([received_at_utc] >= [as_of_utc]),
            CONSTRAINT [ck_valuation_marks_source_reference]
                CHECK
                (
                    ([source_type] = 'MARKET_CANDLE' AND [market_candle_id] IS NOT NULL)
                    OR ([source_type] <> 'MARKET_CANDLE')
                )
        );

        CREATE INDEX [ix_valuation_marks_latest]
            ON [portfolio].[valuation_marks]
            ([instrument_id], [as_of_utc] DESC)
            INCLUDE ([mark_price], [quality_status], [is_stale], [eligible_for_risk]);
    END;

    IF OBJECT_ID(N'[portfolio].[position_valuations]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[position_valuations]
        (
            [position_valuation_id] bigint IDENTITY(1,1) NOT NULL,
            [position_valuation_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_position_valuations_uid] DEFAULT NEWSEQUENTIALID(),
            [position_id] bigint NOT NULL,
            [valuation_mark_id] bigint NOT NULL,
            [position_version] int NOT NULL,
            [position_side] varchar(10) NOT NULL,
            [quantity] decimal(19,6) NOT NULL,
            [average_open_price] decimal(19,6) NULL,
            [mark_price] decimal(19,6) NOT NULL,
            [cost_basis_amount] decimal(19,6) NOT NULL,
            [market_value_amount] decimal(19,6) NOT NULL,
            [unrealized_pnl_amount] decimal(19,6) NOT NULL,
            [gross_exposure_amount] decimal(19,6) NOT NULL,
            [net_exposure_amount] decimal(19,6) NOT NULL,
            [valued_at_utc] datetime2(7) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_position_valuations_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_position_valuations]
                PRIMARY KEY CLUSTERED ([position_valuation_id]),
            CONSTRAINT [uq_position_valuations_uid]
                UNIQUE ([position_valuation_uid]),
            CONSTRAINT [uq_position_valuations_mark]
                UNIQUE ([position_id], [valuation_mark_id], [position_version]),
            CONSTRAINT [fk_position_valuations_position]
                FOREIGN KEY ([position_id])
                REFERENCES [portfolio].[positions] ([position_id]),
            CONSTRAINT [fk_position_valuations_mark]
                FOREIGN KEY ([valuation_mark_id])
                REFERENCES [portfolio].[valuation_marks] ([valuation_mark_id]),
            CONSTRAINT [ck_position_valuations_version]
                CHECK ([position_version] >= 0),
            CONSTRAINT [ck_position_valuations_side]
                CHECK ([position_side] IN ('LONG', 'SHORT', 'FLAT')),
            CONSTRAINT [ck_position_valuations_values]
                CHECK
                (
                    [quantity] >= 0
                    AND ([average_open_price] IS NULL OR [average_open_price] > 0)
                    AND [mark_price] > 0
                    AND [cost_basis_amount] >= 0
                    AND [market_value_amount] >= 0
                    AND [gross_exposure_amount] >= 0
                    AND
                    (([quantity] = 0 AND [position_side] = 'FLAT' AND [average_open_price] IS NULL)
                        OR ([quantity] > 0 AND [position_side] IN ('LONG', 'SHORT')
                            AND [average_open_price] IS NOT NULL))
                )
        );

        CREATE INDEX [ix_position_valuations_latest]
            ON [portfolio].[position_valuations]
            ([position_id], [valued_at_utc] DESC)
            INCLUDE
            (
                [quantity], [mark_price], [market_value_amount],
                [unrealized_pnl_amount], [gross_exposure_amount], [net_exposure_amount]
            );
    END;

    IF OBJECT_ID(N'[portfolio].[pnl_snapshots]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[pnl_snapshots]
        (
            [pnl_snapshot_id] bigint IDENTITY(1,1) NOT NULL,
            [pnl_snapshot_uid] uniqueidentifier NOT NULL,
            [portfolio_id] bigint NOT NULL,
            [currency_code] char(3) NOT NULL,
            [realized_pnl_amount] decimal(19,6) NOT NULL,
            [unrealized_pnl_amount] decimal(19,6) NOT NULL,
            [gross_pnl_amount] decimal(19,6) NOT NULL,
            [fees_amount] decimal(19,6) NOT NULL,
            [taxes_amount] decimal(19,6) NOT NULL,
            [net_pnl_amount] decimal(19,6) NOT NULL,
            [gross_exposure_amount] decimal(19,6) NOT NULL,
            [net_exposure_amount] decimal(19,6) NOT NULL,
            [cash_balance_amount] decimal(19,6) NOT NULL,
            [net_liquidation_value_amount] decimal(19,6) NOT NULL,
            [strategy_drawdown_fraction] decimal(9,8) NOT NULL,
            [portfolio_drawdown_fraction] decimal(9,8) NOT NULL,
            [as_of_utc] datetime2(7) NOT NULL,
            [generated_at_utc] datetime2(7) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [raw_snapshot_json] nvarchar(max) NOT NULL,
            [snapshot_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_pnl_snapshots_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_pnl_snapshots]
                PRIMARY KEY CLUSTERED ([pnl_snapshot_id]),
            CONSTRAINT [uq_pnl_snapshots_uid]
                UNIQUE ([pnl_snapshot_uid]),
            CONSTRAINT [uq_pnl_snapshots_point]
                UNIQUE ([portfolio_id], [as_of_utc], [source_version]),
            CONSTRAINT [fk_pnl_snapshots_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [ck_pnl_snapshots_currency]
                CHECK (LEN(RTRIM([currency_code])) = 3),
            CONSTRAINT [ck_pnl_snapshots_values]
                CHECK
                (
                    [fees_amount] >= 0
                    AND [taxes_amount] >= 0
                    AND [gross_exposure_amount] >= 0
                    AND [strategy_drawdown_fraction] >= 0
                    AND [portfolio_drawdown_fraction] >= 0
                ),
            CONSTRAINT [ck_pnl_snapshots_time]
                CHECK ([generated_at_utc] >= [as_of_utc]),
            CONSTRAINT [ck_pnl_snapshots_json]
                CHECK (ISJSON([raw_snapshot_json]) = 1),
            CONSTRAINT [ck_pnl_snapshots_hash]
                CHECK
                (
                    LEN(RTRIM([snapshot_hash])) = 64
                    AND [snapshot_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE INDEX [ix_pnl_snapshots_latest]
            ON [portfolio].[pnl_snapshots]
            ([portfolio_id], [as_of_utc] DESC)
            INCLUDE
            (
                [realized_pnl_amount], [unrealized_pnl_amount], [net_pnl_amount],
                [gross_exposure_amount], [net_exposure_amount],
                [net_liquidation_value_amount], [portfolio_drawdown_fraction]
            );
    END;

    IF OBJECT_ID(N'[portfolio].[pnl_snapshot_positions]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[pnl_snapshot_positions]
        (
            [pnl_snapshot_position_id] bigint IDENTITY(1,1) NOT NULL,
            [pnl_snapshot_id] bigint NOT NULL,
            [position_id] bigint NOT NULL,
            [position_valuation_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [realized_pnl_amount] decimal(19,6) NOT NULL,
            [unrealized_pnl_amount] decimal(19,6) NOT NULL,
            [fees_amount] decimal(19,6) NOT NULL,
            [taxes_amount] decimal(19,6) NOT NULL,
            [net_pnl_amount] decimal(19,6) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_pnl_snapshot_positions_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_pnl_snapshot_positions]
                PRIMARY KEY CLUSTERED ([pnl_snapshot_position_id]),
            CONSTRAINT [uq_pnl_snapshot_positions_position]
                UNIQUE ([pnl_snapshot_id], [position_id]),
            CONSTRAINT [fk_pnl_snapshot_positions_snapshot]
                FOREIGN KEY ([pnl_snapshot_id])
                REFERENCES [portfolio].[pnl_snapshots] ([pnl_snapshot_id]),
            CONSTRAINT [fk_pnl_snapshot_positions_position]
                FOREIGN KEY ([position_id], [instrument_id])
                REFERENCES [portfolio].[positions] ([position_id], [instrument_id]),
            CONSTRAINT [fk_pnl_snapshot_positions_valuation]
                FOREIGN KEY ([position_valuation_id])
                REFERENCES [portfolio].[position_valuations] ([position_valuation_id]),
            CONSTRAINT [ck_pnl_snapshot_positions_costs]
                CHECK ([fees_amount] >= 0 AND [taxes_amount] >= 0)
        );
    END;

    IF OBJECT_ID(N'[portfolio].[broker_position_observations]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[broker_position_observations]
        (
            [broker_position_observation_id] bigint IDENTITY(1,1) NOT NULL,
            [broker_position_observation_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_broker_position_observations_uid] DEFAULT NEWSEQUENTIALID(),
            [reconciliation_run_id] bigint NOT NULL,
            [reconciliation_observation_id] bigint NULL,
            [broker_account_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [product_type] varchar(30) NOT NULL,
            [position_side] varchar(10) NOT NULL,
            [quantity] decimal(19,6) NOT NULL,
            [average_price] decimal(19,6) NULL,
            [day_buy_quantity] decimal(19,6) NOT NULL,
            [day_sell_quantity] decimal(19,6) NOT NULL,
            [realized_pnl_amount] decimal(19,6) NULL,
            [unrealized_pnl_amount] decimal(19,6) NULL,
            [observed_at_utc] datetime2(7) NOT NULL,
            [retrieved_at_utc] datetime2(7) NOT NULL,
            [source_endpoint] varchar(200) NOT NULL,
            [is_stale] bit NOT NULL,
            [payload_hash] char(64) NOT NULL,
            [redacted_payload_json] nvarchar(max) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_broker_position_observations_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_broker_position_observations]
                PRIMARY KEY CLUSTERED ([broker_position_observation_id]),
            CONSTRAINT [uq_broker_position_observations_uid]
                UNIQUE ([broker_position_observation_uid]),
            CONSTRAINT [uq_broker_position_observations_scope]
                UNIQUE
                ([reconciliation_run_id], [broker_account_id], [instrument_id], [product_type]),
            CONSTRAINT [fk_broker_position_observations_run]
                FOREIGN KEY ([reconciliation_run_id])
                REFERENCES [execution].[reconciliation_runs] ([reconciliation_run_id]),
            CONSTRAINT [fk_broker_position_observations_observation]
                FOREIGN KEY ([reconciliation_observation_id])
                REFERENCES [execution].[reconciliation_observations]
                    ([reconciliation_observation_id]),
            CONSTRAINT [fk_broker_position_observations_account]
                FOREIGN KEY ([broker_account_id])
                REFERENCES [broker].[broker_accounts] ([broker_account_id]),
            CONSTRAINT [fk_broker_position_observations_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [ck_broker_position_observations_product]
                CHECK ([product_type] IN ('CASH', 'DELIVERY', 'INTRADAY', 'FUTURE', 'OPTION')),
            CONSTRAINT [ck_broker_position_observations_side]
                CHECK ([position_side] IN ('LONG', 'SHORT', 'FLAT')),
            CONSTRAINT [ck_broker_position_observations_values]
                CHECK
                (
                    [quantity] >= 0
                    AND ([average_price] IS NULL OR [average_price] > 0)
                    AND [day_buy_quantity] >= 0
                    AND [day_sell_quantity] >= 0
                    AND
                    (([quantity] = 0 AND [position_side] = 'FLAT' AND [average_price] IS NULL)
                        OR ([quantity] > 0 AND [position_side] IN ('LONG', 'SHORT')
                            AND [average_price] IS NOT NULL))
                ),
            CONSTRAINT [ck_broker_position_observations_time]
                CHECK ([retrieved_at_utc] >= [observed_at_utc]),
            CONSTRAINT [ck_broker_position_observations_hash]
                CHECK
                (
                    LEN(RTRIM([payload_hash])) = 64
                    AND [payload_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                ),
            CONSTRAINT [ck_broker_position_observations_json]
                CHECK (ISJSON([redacted_payload_json]) = 1)
        );

        CREATE INDEX [ix_broker_position_observations_latest]
            ON [portfolio].[broker_position_observations]
            ([broker_account_id], [instrument_id], [product_type], [observed_at_utc] DESC)
            INCLUDE ([position_side], [quantity], [average_price], [is_stale]);
    END;

    IF OBJECT_ID(N'[portfolio].[position_reconciliation_states]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[position_reconciliation_states]
        (
            [position_reconciliation_state_id] bigint IDENTITY(1,1) NOT NULL,
            [portfolio_id] bigint NOT NULL,
            [position_id] bigint NULL,
            [broker_account_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [product_type] varchar(30) NOT NULL,
            [last_reconciliation_run_id] bigint NOT NULL,
            [last_broker_position_observation_id] bigint NOT NULL,
            [internal_side] varchar(10) NOT NULL,
            [internal_quantity] decimal(19,6) NOT NULL,
            [broker_side] varchar(10) NOT NULL,
            [broker_quantity] decimal(19,6) NOT NULL,
            [signed_quantity_difference] decimal(19,6) NOT NULL,
            [status] varchar(30) NOT NULL,
            [severity] varchar(20) NOT NULL,
            [blocks_new_exposure] bit NOT NULL,
            [allows_risk_reducing_exits] bit NOT NULL,
            [last_event_sequence] int NOT NULL,
            [detected_at_utc] datetime2(7) NULL,
            [resolved_at_utc] datetime2(7) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_position_reconciliation_states_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_position_reconciliation_states_updated_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_position_reconciliation_states]
                PRIMARY KEY CLUSTERED ([position_reconciliation_state_id]),
            CONSTRAINT [uq_position_reconciliation_states_scope]
                UNIQUE ([portfolio_id], [instrument_id], [product_type]),
            CONSTRAINT [fk_position_reconciliation_states_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [fk_position_reconciliation_states_position]
                FOREIGN KEY ([position_id], [instrument_id])
                REFERENCES [portfolio].[positions] ([position_id], [instrument_id]),
            CONSTRAINT [fk_position_reconciliation_states_account]
                FOREIGN KEY ([broker_account_id])
                REFERENCES [broker].[broker_accounts] ([broker_account_id]),
            CONSTRAINT [fk_position_reconciliation_states_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_position_reconciliation_states_run]
                FOREIGN KEY ([last_reconciliation_run_id])
                REFERENCES [execution].[reconciliation_runs] ([reconciliation_run_id]),
            CONSTRAINT [fk_position_reconciliation_states_observation]
                FOREIGN KEY ([last_broker_position_observation_id])
                REFERENCES [portfolio].[broker_position_observations]
                    ([broker_position_observation_id]),
            CONSTRAINT [ck_position_reconciliation_states_product]
                CHECK ([product_type] IN ('CASH', 'DELIVERY', 'INTRADAY', 'FUTURE', 'OPTION')),
            CONSTRAINT [ck_position_reconciliation_states_sides]
                CHECK
                (
                    [internal_side] IN ('LONG', 'SHORT', 'FLAT')
                    AND [broker_side] IN ('LONG', 'SHORT', 'FLAT')
                ),
            CONSTRAINT [ck_position_reconciliation_states_values]
                CHECK
                (
                    [internal_quantity] >= 0
                    AND [broker_quantity] >= 0
                    AND [last_event_sequence] >= 0
                    AND (([internal_quantity] = 0 AND [internal_side] = 'FLAT')
                        OR ([internal_quantity] > 0 AND [internal_side] IN ('LONG', 'SHORT')))
                    AND (([broker_quantity] = 0 AND [broker_side] = 'FLAT')
                        OR ([broker_quantity] > 0 AND [broker_side] IN ('LONG', 'SHORT')))
                ),
            CONSTRAINT [ck_position_reconciliation_states_status]
                CHECK
                (
                    [status] IN
                    ('IN_SYNC', 'OPEN', 'INVESTIGATING', 'RESOLUTION_PENDING', 'RESOLVED', 'IGNORED')
                ),
            CONSTRAINT [ck_position_reconciliation_states_severity]
                CHECK ([severity] IN ('NONE', 'LOW', 'MEDIUM', 'HIGH', 'CRITICAL')),
            CONSTRAINT [ck_position_reconciliation_states_exit_safety]
                CHECK ([blocks_new_exposure] = 0 OR [allows_risk_reducing_exits] = 1),
            CONSTRAINT [ck_position_reconciliation_states_lifecycle]
                CHECK
                (
                    ([status] = 'IN_SYNC'
                        AND [signed_quantity_difference] = 0
                        AND [detected_at_utc] IS NULL
                        AND [resolved_at_utc] IS NULL)
                    OR
                    ([status] IN ('OPEN', 'INVESTIGATING', 'RESOLUTION_PENDING')
                        AND [detected_at_utc] IS NOT NULL
                        AND [resolved_at_utc] IS NULL)
                    OR
                    ([status] IN ('RESOLVED', 'IGNORED')
                        AND [detected_at_utc] IS NOT NULL
                        AND [resolved_at_utc] IS NOT NULL)
                )
        );

        CREATE INDEX [ix_position_reconciliation_states_open]
            ON [portfolio].[position_reconciliation_states]
            ([status], [severity], [updated_at_utc] DESC)
            INCLUDE
            (
                [portfolio_id], [instrument_id], [product_type],
                [signed_quantity_difference], [blocks_new_exposure]
            );
    END;

    IF OBJECT_ID(N'[portfolio].[position_reconciliation_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[position_reconciliation_events]
        (
            [position_reconciliation_event_id] bigint IDENTITY(1,1) NOT NULL,
            [position_reconciliation_event_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_position_reconciliation_events_uid] DEFAULT NEWSEQUENTIALID(),
            [position_reconciliation_state_id] bigint NOT NULL,
            [reconciliation_run_id] bigint NOT NULL,
            [broker_position_observation_id] bigint NOT NULL,
            [resulting_position_event_id] bigint NULL,
            [event_sequence] int NOT NULL,
            [event_type] varchar(40) NOT NULL,
            [internal_quantity_before] decimal(19,6) NOT NULL,
            [broker_quantity_before] decimal(19,6) NOT NULL,
            [internal_quantity_after] decimal(19,6) NOT NULL,
            [broker_quantity_after] decimal(19,6) NOT NULL,
            [reason_code] varchar(100) NOT NULL,
            [reason_message] nvarchar(2000) NOT NULL,
            [occurred_at_utc] datetime2(7) NOT NULL,
            [approved_by] nvarchar(256) NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_position_reconciliation_events_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_position_reconciliation_events]
                PRIMARY KEY CLUSTERED ([position_reconciliation_event_id]),
            CONSTRAINT [uq_position_reconciliation_events_uid]
                UNIQUE ([position_reconciliation_event_uid]),
            CONSTRAINT [uq_position_reconciliation_events_sequence]
                UNIQUE ([position_reconciliation_state_id], [event_sequence]),
            CONSTRAINT [fk_position_reconciliation_events_state]
                FOREIGN KEY ([position_reconciliation_state_id])
                REFERENCES [portfolio].[position_reconciliation_states]
                    ([position_reconciliation_state_id]),
            CONSTRAINT [fk_position_reconciliation_events_run]
                FOREIGN KEY ([reconciliation_run_id])
                REFERENCES [execution].[reconciliation_runs] ([reconciliation_run_id]),
            CONSTRAINT [fk_position_reconciliation_events_observation]
                FOREIGN KEY ([broker_position_observation_id])
                REFERENCES [portfolio].[broker_position_observations]
                    ([broker_position_observation_id]),
            CONSTRAINT [fk_position_reconciliation_events_position_event]
                FOREIGN KEY ([resulting_position_event_id])
                REFERENCES [portfolio].[position_events] ([position_event_id]),
            CONSTRAINT [ck_position_reconciliation_events_sequence]
                CHECK ([event_sequence] >= 1),
            CONSTRAINT [ck_position_reconciliation_events_type]
                CHECK
                (
                    [event_type] IN
                    ('DETECTED', 'CONFIRMED', 'RESOLUTION_PROPOSED', 'ADJUSTMENT_APPLIED',
                     'RESOLVED', 'IGNORED', 'REOPENED')
                ),
            CONSTRAINT [ck_position_reconciliation_events_quantities]
                CHECK
                (
                    [internal_quantity_before] >= 0
                    AND [broker_quantity_before] >= 0
                    AND [internal_quantity_after] >= 0
                    AND [broker_quantity_after] >= 0
                ),
            CONSTRAINT [ck_position_reconciliation_events_approval]
                CHECK
                (
                    [event_type] NOT IN ('ADJUSTMENT_APPLIED', 'RESOLVED', 'IGNORED')
                    OR [approved_by] IS NOT NULL
                ),
            CONSTRAINT [ck_position_reconciliation_events_adjustment]
                CHECK
                (
                    [event_type] <> 'ADJUSTMENT_APPLIED'
                    OR [resulting_position_event_id] IS NOT NULL
                ),
            CONSTRAINT [ck_position_reconciliation_events_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE INDEX [ix_position_reconciliation_events_latest]
            ON [portfolio].[position_reconciliation_events]
            ([position_reconciliation_state_id], [event_sequence] DESC)
            INCLUDE ([event_type], [occurred_at_utc], [resulting_position_event_id]);
    END;

    UPDATE [operations].[database_metadata]
    SET
        [schema_baseline_version] = 'V0008',
        [updated_at_utc] = SYSUTCDATETIME(),
        [updated_by] = COALESCE(SUSER_SNAME(), N'UNKNOWN')
    WHERE [database_metadata_id] = 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
