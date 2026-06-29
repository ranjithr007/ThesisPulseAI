/*
Migration: V0006__create_risk_and_trade_plan_tables.sql
Purpose:
  Create immutable risk-policy definitions, active-policy assignments, capital and
  portfolio snapshots, deterministic risk decisions, limit evidence and immutable
  trade plans with targets and append-only status history.
Dependencies:
  V0001__create_schemas_and_migration_metadata.sql
  V0002__create_reference_tables.sql
  V0003__create_market_data_tables.sql
  V0004__create_intelligence_and_signal_tables.sql
  V0005__create_thesis_tables.sql
Expected runtime impact:
  Additive DDL only. No existing decision data is scanned or backfilled.
Locking considerations:
  Schema modification locks are acquired while tables, constraints and indexes are created.
Backward-compatibility window:
  Fully additive.
Data migration requirements:
  None.
Verification script:
  database/verification/V0006__verify_risk_and_trade_plan_tables.sql
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

    IF SCHEMA_ID(N'risk') IS NULL
        THROW 56001, 'V0001 is required: schema risk does not exist.', 1;

    IF OBJECT_ID(N'[reference].[instruments]', N'U') IS NULL
        THROW 56002, 'V0002 is required: reference.instruments does not exist.', 1;

    IF OBJECT_ID(N'[intelligence].[signals]', N'U') IS NULL
        THROW 56003, 'V0004 is required: intelligence.signals does not exist.', 1;

    IF OBJECT_ID(N'[thesis].[theses]', N'U') IS NULL
        THROW 56004, 'V0005 is required: thesis.theses does not exist.', 1;

    IF OBJECT_ID(N'[risk].[risk_policies]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[risk_policies]
        (
            [risk_policy_id] bigint IDENTITY(1,1) NOT NULL,
            [risk_policy_uid] uniqueidentifier NOT NULL,
            [contract_version] varchar(20) NOT NULL,
            [risk_policy_version] varchar(100) NOT NULL,
            [initial_status] varchar(20) NOT NULL,
            [environment] varchar(30) NOT NULL,
            [parent_policy_uid] uniqueidentifier NULL,
            [scope_type] varchar(30) NOT NULL,
            [scope_id] varchar(200) NOT NULL,
            [effective_from_utc] datetime2(7) NOT NULL,
            [effective_to_utc] datetime2(7) NULL,
            [standard_risk_per_trade_fraction] decimal(9,8) NOT NULL,
            [maximum_risk_per_trade_fraction] decimal(9,8) NOT NULL,
            [maximum_total_open_risk_fraction] decimal(9,8) NOT NULL,
            [daily_soft_loss_fraction] decimal(9,8) NOT NULL,
            [daily_hard_loss_fraction] decimal(9,8) NOT NULL,
            [weekly_loss_fraction] decimal(9,8) NOT NULL,
            [maximum_strategy_drawdown_fraction] decimal(9,8) NOT NULL,
            [maximum_portfolio_drawdown_fraction] decimal(9,8) NOT NULL,
            [consecutive_loss_pause_count] int NOT NULL,
            [maximum_trades_per_symbol_per_session] int NOT NULL,
            [maximum_sector_exposure_fraction] decimal(9,8) NULL,
            [maximum_correlated_exposure_fraction] decimal(9,8) NULL,
            [maximum_margin_utilization_fraction] decimal(9,8) NULL,
            [maximum_single_instrument_notional_fraction] decimal(9,8) NULL,
            [maximum_gross_exposure_fraction] decimal(19,8) NULL,
            [maximum_net_exposure_fraction] decimal(19,8) NULL,
            [soft_operating_mode] varchar(20) NOT NULL,
            [soft_risk_multiplier] decimal(9,8) NOT NULL,
            [soft_maximum_concurrent_new_positions] int NOT NULL,
            [soft_requires_operator_approval] bit NULL,
            [hard_operating_mode] varchar(20) NOT NULL,
            [hard_allow_risk_reducing_exits] bit NOT NULL,
            [hard_requires_reconciliation_before_reset] bit NULL,
            [hard_requires_operator_approval_to_reset] bit NULL,
            [consecutive_loss_operating_mode] varchar(20) NOT NULL,
            [consecutive_loss_trigger_outcome_attribution] bit NOT NULL,
            [consecutive_loss_minimum_cooling_off_minutes] int NOT NULL,
            [consecutive_loss_requires_health_check] bit NULL,
            [consecutive_loss_requires_operator_approval] bit NULL,
            [created_at_utc] datetime2(7) NOT NULL,
            [created_by] nvarchar(200) NOT NULL,
            [approved_at_utc] datetime2(7) NULL,
            [approved_by] nvarchar(200) NULL,
            [checksum] varchar(256) NOT NULL,
            [metadata_json] nvarchar(max) NULL,
            [raw_contract_json] nvarchar(max) NOT NULL,
            [created_record_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_risk_policies_created_record_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_record_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_risk_policies]
                PRIMARY KEY CLUSTERED ([risk_policy_id]),
            CONSTRAINT [uq_risk_policies_uid]
                UNIQUE ([risk_policy_uid]),
            CONSTRAINT [uq_risk_policies_version]
                UNIQUE ([risk_policy_version], [environment], [scope_type], [scope_id]),
            CONSTRAINT [fk_risk_policies_parent]
                FOREIGN KEY ([parent_policy_uid])
                REFERENCES [risk].[risk_policies] ([risk_policy_uid]),
            CONSTRAINT [ck_risk_policies_contract_version]
                CHECK ([contract_version] = '1.0.0'),
            CONSTRAINT [ck_risk_policies_status]
                CHECK
                (
                    [initial_status] IN
                    ('DRAFT', 'APPROVED', 'ACTIVE', 'SUSPENDED', 'RETIRED', 'REJECTED')
                ),
            CONSTRAINT [ck_risk_policies_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'RESTRICTED_LIVE', 'LIVE')),
            CONSTRAINT [ck_risk_policies_scope]
                CHECK
                (
                    [scope_type] IN
                    (
                        'GLOBAL', 'ENVIRONMENT', 'BROKER_ACCOUNT', 'STRATEGY',
                        'INSTRUMENT', 'SECTOR', 'PRODUCT_TYPE', 'SESSION', 'MODEL_VERSION'
                    )
                ),
            CONSTRAINT [ck_risk_policies_effective_window]
                CHECK
                (
                    [effective_to_utc] IS NULL
                    OR [effective_to_utc] > [effective_from_utc]
                ),
            CONSTRAINT [ck_risk_policies_core_limits]
                CHECK
                (
                    [standard_risk_per_trade_fraction] BETWEEN 0 AND 1
                    AND [maximum_risk_per_trade_fraction] BETWEEN 0 AND 1
                    AND [maximum_total_open_risk_fraction] BETWEEN 0 AND 1
                    AND [daily_soft_loss_fraction] BETWEEN 0 AND 1
                    AND [daily_hard_loss_fraction] BETWEEN 0 AND 1
                    AND [weekly_loss_fraction] BETWEEN 0 AND 1
                    AND [maximum_strategy_drawdown_fraction] BETWEEN 0 AND 1
                    AND [maximum_portfolio_drawdown_fraction] BETWEEN 0 AND 1
                    AND [standard_risk_per_trade_fraction] <= [maximum_risk_per_trade_fraction]
                    AND [daily_soft_loss_fraction] <= [daily_hard_loss_fraction]
                    AND [consecutive_loss_pause_count] >= 1
                    AND [maximum_trades_per_symbol_per_session] >= 1
                ),
            CONSTRAINT [ck_risk_policies_optional_fraction_limits]
                CHECK
                (
                    ([maximum_sector_exposure_fraction] IS NULL
                        OR [maximum_sector_exposure_fraction] BETWEEN 0 AND 1)
                    AND ([maximum_correlated_exposure_fraction] IS NULL
                        OR [maximum_correlated_exposure_fraction] BETWEEN 0 AND 1)
                    AND ([maximum_margin_utilization_fraction] IS NULL
                        OR [maximum_margin_utilization_fraction] BETWEEN 0 AND 1)
                    AND ([maximum_single_instrument_notional_fraction] IS NULL
                        OR [maximum_single_instrument_notional_fraction] BETWEEN 0 AND 1)
                    AND ([maximum_gross_exposure_fraction] IS NULL
                        OR [maximum_gross_exposure_fraction] >= 0)
                ),
            CONSTRAINT [ck_risk_policies_soft_response]
                CHECK
                (
                    [soft_operating_mode] IN ('RESTRICTED', 'CLOSE_ONLY', 'PAUSED')
                    AND [soft_risk_multiplier] BETWEEN 0 AND 1
                    AND [soft_maximum_concurrent_new_positions] >= 0
                ),
            CONSTRAINT [ck_risk_policies_hard_response]
                CHECK
                (
                    [hard_operating_mode] IN ('CLOSE_ONLY', 'PAUSED', 'HALTED')
                    AND [hard_allow_risk_reducing_exits] = 1
                ),
            CONSTRAINT [ck_risk_policies_consecutive_loss_response]
                CHECK
                (
                    [consecutive_loss_operating_mode] IN
                    ('PAUSED', 'RESTRICTED', 'CLOSE_ONLY')
                    AND [consecutive_loss_minimum_cooling_off_minutes] >= 0
                ),
            CONSTRAINT [ck_risk_policies_approval]
                CHECK
                (
                    ([initial_status] IN ('DRAFT', 'REJECTED')
                        AND [approved_at_utc] IS NULL
                        AND [approved_by] IS NULL)
                    OR
                    ([initial_status] NOT IN ('DRAFT', 'REJECTED')
                        AND [approved_at_utc] IS NOT NULL
                        AND [approved_by] IS NOT NULL)
                ),
            CONSTRAINT [ck_risk_policies_not_self_parent]
                CHECK ([parent_policy_uid] IS NULL OR [parent_policy_uid] <> [risk_policy_uid]),
            CONSTRAINT [ck_risk_policies_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_risk_policies_raw_contract_json]
                CHECK (ISJSON([raw_contract_json]) = 1),
            CONSTRAINT [ck_risk_policies_checksum]
                CHECK (LEN([checksum]) BETWEEN 16 AND 256)
        );

        CREATE INDEX [ix_risk_policies_scope]
            ON [risk].[risk_policies]
            ([environment], [scope_type], [scope_id], [effective_from_utc] DESC)
            INCLUDE ([risk_policy_version], [initial_status], [effective_to_utc]);

        CREATE INDEX [ix_risk_policies_parent]
            ON [risk].[risk_policies] ([parent_policy_uid], [risk_policy_version]);
    END;

    IF OBJECT_ID(N'[risk].[risk_policy_mandatory_rules]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[risk_policy_mandatory_rules]
        (
            [risk_policy_mandatory_rule_id] bigint IDENTITY(1,1) NOT NULL,
            [risk_policy_id] bigint NOT NULL,
            [rule_sequence] int NOT NULL,
            [rule_code] varchar(100) NOT NULL,
            [rule_description] nvarchar(1000) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_risk_policy_rules_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_risk_policy_mandatory_rules]
                PRIMARY KEY CLUSTERED ([risk_policy_mandatory_rule_id]),
            CONSTRAINT [uq_risk_policy_rules_sequence]
                UNIQUE ([risk_policy_id], [rule_sequence]),
            CONSTRAINT [uq_risk_policy_rules_code]
                UNIQUE ([risk_policy_id], [rule_code]),
            CONSTRAINT [fk_risk_policy_rules_policy]
                FOREIGN KEY ([risk_policy_id])
                REFERENCES [risk].[risk_policies] ([risk_policy_id]),
            CONSTRAINT [ck_risk_policy_rules_sequence]
                CHECK ([rule_sequence] >= 1)
        );
    END;

    IF OBJECT_ID(N'[risk].[risk_policy_status_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[risk_policy_status_events]
        (
            [risk_policy_status_event_id] bigint IDENTITY(1,1) NOT NULL,
            [risk_policy_status_event_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_risk_policy_status_events_uid] DEFAULT NEWSEQUENTIALID(),
            [risk_policy_id] bigint NOT NULL,
            [event_sequence] int NOT NULL,
            [status] varchar(20) NOT NULL,
            [reason_codes_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_risk_policy_status_events_reasons] DEFAULT (N'[]'),
            [occurred_at_utc] datetime2(7) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_risk_policy_status_events_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_risk_policy_status_events]
                PRIMARY KEY CLUSTERED ([risk_policy_status_event_id]),
            CONSTRAINT [uq_risk_policy_status_events_uid]
                UNIQUE ([risk_policy_status_event_uid]),
            CONSTRAINT [uq_risk_policy_status_events_sequence]
                UNIQUE ([risk_policy_id], [event_sequence]),
            CONSTRAINT [fk_risk_policy_status_events_policy]
                FOREIGN KEY ([risk_policy_id])
                REFERENCES [risk].[risk_policies] ([risk_policy_id]),
            CONSTRAINT [ck_risk_policy_status_events_sequence]
                CHECK ([event_sequence] >= 0),
            CONSTRAINT [ck_risk_policy_status_events_status]
                CHECK
                (
                    [status] IN
                    ('DRAFT', 'APPROVED', 'ACTIVE', 'SUSPENDED', 'RETIRED', 'REJECTED')
                ),
            CONSTRAINT [ck_risk_policy_status_events_reasons_json]
                CHECK (ISJSON([reason_codes_json]) = 1)
        );

        CREATE INDEX [ix_risk_policy_status_events_latest]
            ON [risk].[risk_policy_status_events]
            ([risk_policy_id], [event_sequence] DESC)
            INCLUDE ([status], [occurred_at_utc]);
    END;

    IF OBJECT_ID(N'[risk].[active_policy_assignments]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[active_policy_assignments]
        (
            [active_policy_assignment_id] bigint IDENTITY(1,1) NOT NULL,
            [risk_policy_id] bigint NOT NULL,
            [environment] varchar(30) NOT NULL,
            [scope_type] varchar(30) NOT NULL,
            [scope_id] varchar(200) NOT NULL,
            [assignment_status] varchar(20) NOT NULL,
            [active_from_utc] datetime2(7) NOT NULL,
            [active_to_utc] datetime2(7) NULL,
            [assigned_at_utc] datetime2(7) NOT NULL,
            [assigned_by] nvarchar(200) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_active_policy_assignments_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_active_policy_assignments]
                PRIMARY KEY CLUSTERED ([active_policy_assignment_id]),
            CONSTRAINT [fk_active_policy_assignments_policy]
                FOREIGN KEY ([risk_policy_id])
                REFERENCES [risk].[risk_policies] ([risk_policy_id]),
            CONSTRAINT [ck_active_policy_assignments_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'RESTRICTED_LIVE', 'LIVE')),
            CONSTRAINT [ck_active_policy_assignments_scope]
                CHECK
                (
                    [scope_type] IN
                    (
                        'GLOBAL', 'ENVIRONMENT', 'BROKER_ACCOUNT', 'STRATEGY',
                        'INSTRUMENT', 'SECTOR', 'PRODUCT_TYPE', 'SESSION', 'MODEL_VERSION'
                    )
                ),
            CONSTRAINT [ck_active_policy_assignments_status]
                CHECK ([assignment_status] IN ('ACTIVE', 'SUSPENDED', 'RETIRED')),
            CONSTRAINT [ck_active_policy_assignments_window]
                CHECK
                (
                    [active_to_utc] IS NULL
                    OR [active_to_utc] > [active_from_utc]
                )
        );

        CREATE UNIQUE INDEX [ux_active_policy_assignments_open]
            ON [risk].[active_policy_assignments]
            ([environment], [scope_type], [scope_id])
            WHERE [assignment_status] = 'ACTIVE' AND [active_to_utc] IS NULL;

        CREATE INDEX [ix_active_policy_assignments_policy]
            ON [risk].[active_policy_assignments]
            ([risk_policy_id], [active_from_utc] DESC);
    END;

    IF OBJECT_ID(N'[risk].[capital_snapshots]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[capital_snapshots]
        (
            [capital_snapshot_id] bigint IDENTITY(1,1) NOT NULL,
            [capital_snapshot_uid] uniqueidentifier NOT NULL,
            [environment] varchar(20) NOT NULL,
            [broker_account_reference] varchar(200) NOT NULL,
            [currency_code] char(3) NOT NULL,
            [eligible_capital_amount] decimal(19,6) NOT NULL,
            [cash_balance_amount] decimal(19,6) NOT NULL,
            [available_capital_amount] decimal(19,6) NOT NULL,
            [buying_power_amount] decimal(19,6) NULL,
            [available_margin_amount] decimal(19,6) NULL,
            [utilized_margin_amount] decimal(19,6) NULL,
            [realized_pnl_amount] decimal(19,6) NOT NULL,
            [unrealized_pnl_amount] decimal(19,6) NOT NULL,
            [accrued_fees_amount] decimal(19,6) NOT NULL,
            [as_of_utc] datetime2(7) NOT NULL,
            [captured_at_utc] datetime2(7) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [source_snapshot_reference] varchar(200) NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [raw_snapshot_json] nvarchar(max) NOT NULL,
            [snapshot_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_capital_snapshots_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_capital_snapshots]
                PRIMARY KEY CLUSTERED ([capital_snapshot_id]),
            CONSTRAINT [uq_capital_snapshots_uid]
                UNIQUE ([capital_snapshot_uid]),
            CONSTRAINT [uq_capital_snapshots_source_reference]
                UNIQUE ([environment], [broker_account_reference], [source_snapshot_reference]),
            CONSTRAINT [ck_capital_snapshots_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_capital_snapshots_currency]
                CHECK (LEN(RTRIM([currency_code])) = 3),
            CONSTRAINT [ck_capital_snapshots_amounts]
                CHECK
                (
                    [eligible_capital_amount] >= 0
                    AND [cash_balance_amount] >= 0
                    AND [available_capital_amount] >= 0
                    AND ([buying_power_amount] IS NULL OR [buying_power_amount] >= 0)
                    AND ([available_margin_amount] IS NULL OR [available_margin_amount] >= 0)
                    AND ([utilized_margin_amount] IS NULL OR [utilized_margin_amount] >= 0)
                    AND [accrued_fees_amount] >= 0
                ),
            CONSTRAINT [ck_capital_snapshots_time]
                CHECK ([captured_at_utc] >= [as_of_utc]),
            CONSTRAINT [ck_capital_snapshots_raw_json]
                CHECK (ISJSON([raw_snapshot_json]) = 1),
            CONSTRAINT [ck_capital_snapshots_hash]
                CHECK
                (
                    LEN(RTRIM([snapshot_hash])) = 64
                    AND [snapshot_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE INDEX [ix_capital_snapshots_latest]
            ON [risk].[capital_snapshots]
            ([environment], [broker_account_reference], [as_of_utc] DESC)
            INCLUDE
            (
                [eligible_capital_amount], [available_capital_amount],
                [available_margin_amount], [realized_pnl_amount], [unrealized_pnl_amount]
            );
    END;

    IF OBJECT_ID(N'[risk].[portfolio_snapshots]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[portfolio_snapshots]
        (
            [portfolio_snapshot_id] bigint IDENTITY(1,1) NOT NULL,
            [portfolio_snapshot_uid] uniqueidentifier NOT NULL,
            [environment] varchar(20) NOT NULL,
            [broker_account_reference] varchar(200) NOT NULL,
            [strategy_code] varchar(100) NULL,
            [currency_code] char(3) NOT NULL,
            [gross_exposure_amount] decimal(19,6) NOT NULL,
            [net_exposure_amount] decimal(19,6) NOT NULL,
            [total_open_risk_amount] decimal(19,6) NOT NULL,
            [total_open_risk_fraction] decimal(9,8) NOT NULL,
            [daily_pnl_amount] decimal(19,6) NOT NULL,
            [weekly_pnl_amount] decimal(19,6) NOT NULL,
            [strategy_drawdown_fraction] decimal(9,8) NOT NULL,
            [portfolio_drawdown_fraction] decimal(9,8) NOT NULL,
            [consecutive_losses] int NOT NULL,
            [open_position_count] int NOT NULL,
            [new_trades_this_session] int NOT NULL,
            [as_of_utc] datetime2(7) NOT NULL,
            [captured_at_utc] datetime2(7) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [source_snapshot_reference] varchar(200) NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [raw_snapshot_json] nvarchar(max) NOT NULL,
            [snapshot_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_snapshots_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_portfolio_snapshots]
                PRIMARY KEY CLUSTERED ([portfolio_snapshot_id]),
            CONSTRAINT [uq_portfolio_snapshots_uid]
                UNIQUE ([portfolio_snapshot_uid]),
            CONSTRAINT [uq_portfolio_snapshots_source_reference]
                UNIQUE ([environment], [broker_account_reference], [source_snapshot_reference]),
            CONSTRAINT [ck_portfolio_snapshots_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_portfolio_snapshots_currency]
                CHECK (LEN(RTRIM([currency_code])) = 3),
            CONSTRAINT [ck_portfolio_snapshots_values]
                CHECK
                (
                    [gross_exposure_amount] >= 0
                    AND [total_open_risk_amount] >= 0
                    AND [total_open_risk_fraction] >= 0
                    AND [strategy_drawdown_fraction] >= 0
                    AND [portfolio_drawdown_fraction] >= 0
                    AND [consecutive_losses] >= 0
                    AND [open_position_count] >= 0
                    AND [new_trades_this_session] >= 0
                ),
            CONSTRAINT [ck_portfolio_snapshots_time]
                CHECK ([captured_at_utc] >= [as_of_utc]),
            CONSTRAINT [ck_portfolio_snapshots_raw_json]
                CHECK (ISJSON([raw_snapshot_json]) = 1),
            CONSTRAINT [ck_portfolio_snapshots_hash]
                CHECK
                (
                    LEN(RTRIM([snapshot_hash])) = 64
                    AND [snapshot_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE INDEX [ix_portfolio_snapshots_latest]
            ON [risk].[portfolio_snapshots]
            ([environment], [broker_account_reference], [strategy_code], [as_of_utc] DESC)
            INCLUDE
            (
                [gross_exposure_amount], [net_exposure_amount],
                [total_open_risk_fraction], [daily_pnl_amount], [weekly_pnl_amount],
                [strategy_drawdown_fraction], [portfolio_drawdown_fraction]
            );
    END;

    IF OBJECT_ID(N'[risk].[portfolio_snapshot_positions]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[portfolio_snapshot_positions]
        (
            [portfolio_snapshot_position_id] bigint IDENTITY(1,1) NOT NULL,
            [portfolio_snapshot_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [product_type] varchar(30) NOT NULL,
            [position_side] varchar(10) NOT NULL,
            [quantity] decimal(19,6) NOT NULL,
            [average_price] decimal(19,6) NULL,
            [current_price] decimal(19,6) NULL,
            [market_value_amount] decimal(19,6) NOT NULL,
            [unrealized_pnl_amount] decimal(19,6) NOT NULL,
            [protective_stop_price] decimal(19,6) NULL,
            [open_risk_amount] decimal(19,6) NOT NULL,
            [open_risk_fraction] decimal(9,8) NOT NULL,
            [sector_code] varchar(100) NULL,
            [correlation_bucket] varchar(100) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_snapshot_positions_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_portfolio_snapshot_positions]
                PRIMARY KEY CLUSTERED ([portfolio_snapshot_position_id]),
            CONSTRAINT [uq_portfolio_snapshot_positions_scope]
                UNIQUE ([portfolio_snapshot_id], [instrument_id], [product_type]),
            CONSTRAINT [fk_portfolio_snapshot_positions_snapshot]
                FOREIGN KEY ([portfolio_snapshot_id])
                REFERENCES [risk].[portfolio_snapshots] ([portfolio_snapshot_id]),
            CONSTRAINT [fk_portfolio_snapshot_positions_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [ck_portfolio_snapshot_positions_product]
                CHECK ([product_type] IN ('CASH', 'DELIVERY', 'INTRADAY', 'FUTURE', 'OPTION')),
            CONSTRAINT [ck_portfolio_snapshot_positions_side]
                CHECK ([position_side] IN ('LONG', 'SHORT', 'FLAT')),
            CONSTRAINT [ck_portfolio_snapshot_positions_values]
                CHECK
                (
                    [quantity] >= 0
                    AND ([average_price] IS NULL OR [average_price] > 0)
                    AND ([current_price] IS NULL OR [current_price] > 0)
                    AND ([protective_stop_price] IS NULL OR [protective_stop_price] > 0)
                    AND [open_risk_amount] >= 0
                    AND [open_risk_fraction] >= 0
                    AND
                    (
                        ([position_side] = 'FLAT' AND [quantity] = 0)
                        OR ([position_side] <> 'FLAT' AND [quantity] > 0)
                    )
                )
        );

        CREATE INDEX [ix_portfolio_snapshot_positions_instrument]
            ON [risk].[portfolio_snapshot_positions]
            ([instrument_id], [portfolio_snapshot_id]);
    END;

    IF OBJECT_ID(N'[risk].[portfolio_snapshot_exposures]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[portfolio_snapshot_exposures]
        (
            [portfolio_snapshot_exposure_id] bigint IDENTITY(1,1) NOT NULL,
            [portfolio_snapshot_id] bigint NOT NULL,
            [exposure_type] varchar(30) NOT NULL,
            [scope_id] varchar(200) NOT NULL,
            [exposure_amount] decimal(19,6) NOT NULL,
            [exposure_fraction] decimal(19,8) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_snapshot_exposures_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_portfolio_snapshot_exposures]
                PRIMARY KEY CLUSTERED ([portfolio_snapshot_exposure_id]),
            CONSTRAINT [uq_portfolio_snapshot_exposures_scope]
                UNIQUE ([portfolio_snapshot_id], [exposure_type], [scope_id]),
            CONSTRAINT [fk_portfolio_snapshot_exposures_snapshot]
                FOREIGN KEY ([portfolio_snapshot_id])
                REFERENCES [risk].[portfolio_snapshots] ([portfolio_snapshot_id]),
            CONSTRAINT [ck_portfolio_snapshot_exposures_type]
                CHECK
                (
                    [exposure_type] IN
                    ('GROSS', 'NET', 'INSTRUMENT', 'SECTOR', 'CORRELATED', 'PRODUCT_TYPE')
                ),
            CONSTRAINT [ck_portfolio_snapshot_exposures_values]
                CHECK
                (
                    ([exposure_type] = 'NET' OR [exposure_amount] >= 0)
                    AND ([exposure_type] = 'NET'
                        OR [exposure_fraction] IS NULL
                        OR [exposure_fraction] >= 0)
                )
        );
    END;

    IF OBJECT_ID(N'[risk].[risk_decisions]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[risk_decisions]
        (
            [risk_decision_id] bigint IDENTITY(1,1) NOT NULL,
            [risk_decision_uid] uniqueidentifier NOT NULL,
            [message_uid] uniqueidentifier NOT NULL,
            [signal_id] bigint NOT NULL,
            [thesis_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [risk_policy_id] bigint NOT NULL,
            [capital_snapshot_id] bigint NOT NULL,
            [portfolio_snapshot_id] bigint NOT NULL,
            [contract_version] varchar(20) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [decision] varchar(20) NOT NULL,
            [risk_policy_version_snapshot] varchar(100) NOT NULL,
            [requested_risk_fraction] decimal(9,8) NOT NULL,
            [requested_risk_amount] decimal(19,6) NOT NULL,
            [approved_risk_fraction] decimal(9,8) NOT NULL,
            [approved_risk_amount] decimal(19,6) NOT NULL,
            [requested_quantity] decimal(19,6) NOT NULL,
            [approved_quantity] decimal(19,6) NOT NULL,
            [entry_price] decimal(19,6) NOT NULL,
            [stop_loss_price] decimal(19,6) NOT NULL,
            [estimated_margin_amount] decimal(19,6) NULL,
            [estimated_fees_amount] decimal(19,6) NULL,
            [estimated_slippage_amount] decimal(19,6) NULL,
            [risk_reward_ratio] decimal(19,8) NULL,
            [daily_pnl_amount] decimal(19,6) NOT NULL,
            [weekly_pnl_amount] decimal(19,6) NOT NULL,
            [strategy_drawdown_fraction] decimal(9,8) NOT NULL,
            [portfolio_drawdown_fraction] decimal(9,8) NOT NULL,
            [total_open_risk_fraction] decimal(9,8) NOT NULL,
            [consecutive_losses] int NOT NULL,
            [available_capital_amount] decimal(19,6) NULL,
            [available_margin_amount] decimal(19,6) NULL,
            [gross_exposure_amount] decimal(19,6) NULL,
            [net_exposure_amount] decimal(19,6) NULL,
            [instrument_exposure_amount] decimal(19,6) NULL,
            [sector_exposure_amount] decimal(19,6) NULL,
            [correlated_exposure_amount] decimal(19,6) NULL,
            [generated_at_utc] datetime2(7) NOT NULL,
            [evaluated_at_utc] datetime2(7) NOT NULL,
            [valid_until_utc] datetime2(7) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [raw_contract_json] nvarchar(max) NOT NULL,
            [contract_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_risk_decisions_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_risk_decisions]
                PRIMARY KEY CLUSTERED ([risk_decision_id]),
            CONSTRAINT [uq_risk_decisions_uid]
                UNIQUE ([risk_decision_uid]),
            CONSTRAINT [uq_risk_decisions_message_uid]
                UNIQUE ([message_uid]),
            CONSTRAINT [uq_risk_decisions_id_instrument]
                UNIQUE ([risk_decision_id], [instrument_id]),
            CONSTRAINT [fk_risk_decisions_signal]
                FOREIGN KEY ([signal_id], [instrument_id])
                REFERENCES [intelligence].[signals] ([signal_id], [instrument_id]),
            CONSTRAINT [fk_risk_decisions_thesis]
                FOREIGN KEY ([thesis_id], [instrument_id])
                REFERENCES [thesis].[theses] ([thesis_id], [instrument_id]),
            CONSTRAINT [fk_risk_decisions_policy]
                FOREIGN KEY ([risk_policy_id])
                REFERENCES [risk].[risk_policies] ([risk_policy_id]),
            CONSTRAINT [fk_risk_decisions_capital_snapshot]
                FOREIGN KEY ([capital_snapshot_id])
                REFERENCES [risk].[capital_snapshots] ([capital_snapshot_id]),
            CONSTRAINT [fk_risk_decisions_portfolio_snapshot]
                FOREIGN KEY ([portfolio_snapshot_id])
                REFERENCES [risk].[portfolio_snapshots] ([portfolio_snapshot_id]),
            CONSTRAINT [ck_risk_decisions_contract_version]
                CHECK ([contract_version] = '1.0.0'),
            CONSTRAINT [ck_risk_decisions_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_risk_decisions_decision]
                CHECK ([decision] IN ('APPROVE', 'REJECT', 'RESTRICT')),
            CONSTRAINT [ck_risk_decisions_risk_values]
                CHECK
                (
                    [requested_risk_fraction] BETWEEN 0 AND 1
                    AND [approved_risk_fraction] BETWEEN 0 AND 1
                    AND [requested_risk_amount] >= 0
                    AND [approved_risk_amount] >= 0
                    AND [approved_risk_fraction] <= [requested_risk_fraction]
                    AND [approved_risk_amount] <= [requested_risk_amount]
                ),
            CONSTRAINT [ck_risk_decisions_quantity]
                CHECK
                (
                    [requested_quantity] >= 0
                    AND [approved_quantity] >= 0
                    AND [approved_quantity] <= [requested_quantity]
                ),
            CONSTRAINT [ck_risk_decisions_decision_outcome]
                CHECK
                (
                    ([decision] = 'REJECT'
                        AND [approved_risk_fraction] = 0
                        AND [approved_risk_amount] = 0
                        AND [approved_quantity] = 0)
                    OR
                    ([decision] IN ('APPROVE', 'RESTRICT')
                        AND [approved_risk_fraction] > 0
                        AND [approved_risk_amount] > 0
                        AND [approved_quantity] > 0)
                ),
            CONSTRAINT [ck_risk_decisions_prices]
                CHECK ([entry_price] > 0 AND [stop_loss_price] > 0),
            CONSTRAINT [ck_risk_decisions_estimates]
                CHECK
                (
                    ([estimated_margin_amount] IS NULL OR [estimated_margin_amount] >= 0)
                    AND ([estimated_fees_amount] IS NULL OR [estimated_fees_amount] >= 0)
                    AND ([estimated_slippage_amount] IS NULL OR [estimated_slippage_amount] >= 0)
                    AND ([risk_reward_ratio] IS NULL OR [risk_reward_ratio] >= 0)
                ),
            CONSTRAINT [ck_risk_decisions_current_limits]
                CHECK
                (
                    [strategy_drawdown_fraction] >= 0
                    AND [portfolio_drawdown_fraction] >= 0
                    AND [total_open_risk_fraction] >= 0
                    AND [consecutive_losses] >= 0
                    AND ([available_capital_amount] IS NULL OR [available_capital_amount] >= 0)
                    AND ([available_margin_amount] IS NULL OR [available_margin_amount] >= 0)
                ),
            CONSTRAINT [ck_risk_decisions_exposure]
                CHECK
                (
                    ([gross_exposure_amount] IS NULL OR [gross_exposure_amount] >= 0)
                    AND ([instrument_exposure_amount] IS NULL OR [instrument_exposure_amount] >= 0)
                    AND ([sector_exposure_amount] IS NULL OR [sector_exposure_amount] >= 0)
                    AND ([correlated_exposure_amount] IS NULL OR [correlated_exposure_amount] >= 0)
                ),
            CONSTRAINT [ck_risk_decisions_time]
                CHECK
                (
                    [evaluated_at_utc] >= [generated_at_utc]
                    AND [valid_until_utc] > [evaluated_at_utc]
                ),
            CONSTRAINT [ck_risk_decisions_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_risk_decisions_raw_contract_json]
                CHECK (ISJSON([raw_contract_json]) = 1),
            CONSTRAINT [ck_risk_decisions_contract_hash]
                CHECK
                (
                    LEN(RTRIM([contract_hash])) = 64
                    AND [contract_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE INDEX [ix_risk_decisions_latest]
            ON [risk].[risk_decisions]
            ([instrument_id], [evaluated_at_utc] DESC)
            INCLUDE
            (
                [signal_id], [thesis_id], [decision], [risk_policy_version_snapshot],
                [approved_risk_fraction], [approved_quantity], [valid_until_utc]
            );

        CREATE INDEX [ix_risk_decisions_policy]
            ON [risk].[risk_decisions]
            ([risk_policy_id], [evaluated_at_utc] DESC);

        CREATE INDEX [ix_risk_decisions_correlation]
            ON [risk].[risk_decisions] ([correlation_id], [evaluated_at_utc]);
    END;

    IF OBJECT_ID(N'[risk].[risk_decision_reason_codes]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[risk_decision_reason_codes]
        (
            [risk_decision_reason_code_id] bigint IDENTITY(1,1) NOT NULL,
            [risk_decision_id] bigint NOT NULL,
            [reason_sequence] int NOT NULL,
            [reason_code] varchar(100) NOT NULL,
            [reason_description] nvarchar(1000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_risk_decision_reasons_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_risk_decision_reason_codes]
                PRIMARY KEY CLUSTERED ([risk_decision_reason_code_id]),
            CONSTRAINT [uq_risk_decision_reasons_sequence]
                UNIQUE ([risk_decision_id], [reason_sequence]),
            CONSTRAINT [uq_risk_decision_reasons_code]
                UNIQUE ([risk_decision_id], [reason_code]),
            CONSTRAINT [fk_risk_decision_reasons_decision]
                FOREIGN KEY ([risk_decision_id])
                REFERENCES [risk].[risk_decisions] ([risk_decision_id]),
            CONSTRAINT [ck_risk_decision_reasons_sequence]
                CHECK ([reason_sequence] >= 1)
        );
    END;

    IF OBJECT_ID(N'[risk].[risk_decision_targets]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[risk_decision_targets]
        (
            [risk_decision_target_id] bigint IDENTITY(1,1) NOT NULL,
            [risk_decision_id] bigint NOT NULL,
            [target_sequence] int NOT NULL,
            [target_price] decimal(19,6) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_risk_decision_targets_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_risk_decision_targets]
                PRIMARY KEY CLUSTERED ([risk_decision_target_id]),
            CONSTRAINT [uq_risk_decision_targets_sequence]
                UNIQUE ([risk_decision_id], [target_sequence]),
            CONSTRAINT [uq_risk_decision_targets_price]
                UNIQUE ([risk_decision_id], [target_price]),
            CONSTRAINT [fk_risk_decision_targets_decision]
                FOREIGN KEY ([risk_decision_id])
                REFERENCES [risk].[risk_decisions] ([risk_decision_id]),
            CONSTRAINT [ck_risk_decision_targets_sequence]
                CHECK ([target_sequence] >= 1),
            CONSTRAINT [ck_risk_decision_targets_price]
                CHECK ([target_price] > 0)
        );
    END;

    IF OBJECT_ID(N'[risk].[risk_decision_limit_checks]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[risk_decision_limit_checks]
        (
            [risk_decision_limit_check_id] bigint IDENTITY(1,1) NOT NULL,
            [risk_decision_id] bigint NOT NULL,
            [check_sequence] int NOT NULL,
            [rule_code] varchar(100) NOT NULL,
            [scope_type] varchar(30) NOT NULL,
            [scope_id] varchar(200) NOT NULL,
            [measurement_type] varchar(20) NOT NULL,
            [limit_value] decimal(19,8) NULL,
            [current_value] decimal(19,8) NULL,
            [projected_value] decimal(19,8) NULL,
            [check_result] varchar(20) NOT NULL,
            [is_hard_limit] bit NOT NULL,
            [reason_code] varchar(100) NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_risk_decision_limit_checks_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_risk_decision_limit_checks]
                PRIMARY KEY CLUSTERED ([risk_decision_limit_check_id]),
            CONSTRAINT [uq_risk_decision_limit_checks_sequence]
                UNIQUE ([risk_decision_id], [check_sequence]),
            CONSTRAINT [uq_risk_decision_limit_checks_rule]
                UNIQUE ([risk_decision_id], [rule_code], [scope_type], [scope_id]),
            CONSTRAINT [fk_risk_decision_limit_checks_decision]
                FOREIGN KEY ([risk_decision_id])
                REFERENCES [risk].[risk_decisions] ([risk_decision_id]),
            CONSTRAINT [ck_risk_decision_limit_checks_sequence]
                CHECK ([check_sequence] >= 1),
            CONSTRAINT [ck_risk_decision_limit_checks_scope]
                CHECK
                (
                    [scope_type] IN
                    (
                        'GLOBAL', 'ENVIRONMENT', 'BROKER_ACCOUNT', 'STRATEGY',
                        'INSTRUMENT', 'SECTOR', 'PRODUCT_TYPE', 'SESSION', 'MODEL_VERSION'
                    )
                ),
            CONSTRAINT [ck_risk_decision_limit_checks_measurement]
                CHECK ([measurement_type] IN ('FRACTION', 'AMOUNT', 'COUNT', 'BOOLEAN')),
            CONSTRAINT [ck_risk_decision_limit_checks_result]
                CHECK ([check_result] IN ('PASS', 'RESTRICT', 'FAIL', 'NOT_APPLICABLE')),
            CONSTRAINT [ck_risk_decision_limit_checks_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE INDEX [ix_risk_decision_limit_checks_result]
            ON [risk].[risk_decision_limit_checks]
            ([check_result], [is_hard_limit], [risk_decision_id]);
    END;

    IF OBJECT_ID(N'[risk].[trade_plans]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[trade_plans]
        (
            [trade_plan_id] bigint IDENTITY(1,1) NOT NULL,
            [trade_plan_uid] uniqueidentifier NOT NULL,
            [message_uid] uniqueidentifier NOT NULL,
            [risk_decision_id] bigint NOT NULL,
            [thesis_id] bigint NOT NULL,
            [signal_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [contract_version] varchar(20) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [plan_version] int NOT NULL,
            [side] varchar(10) NOT NULL,
            [position_intent] varchar(20) NOT NULL,
            [entry_order_type] varchar(20) NOT NULL,
            [entry_reference_price] decimal(19,6) NOT NULL,
            [entry_limit_price] decimal(19,6) NULL,
            [entry_trigger_price] decimal(19,6) NULL,
            [minimum_acceptable_price] decimal(19,6) NULL,
            [maximum_acceptable_price] decimal(19,6) NULL,
            [approved_quantity] decimal(19,6) NOT NULL,
            [minimum_execution_quantity] decimal(19,6) NULL,
            [allow_partial_fill] bit NOT NULL
                CONSTRAINT [df_trade_plans_allow_partial_fill] DEFAULT (1),
            [stop_loss_price] decimal(19,6) NOT NULL,
            [stop_loss_order_type] varchar(20) NOT NULL,
            [stop_loss_limit_price] decimal(19,6) NULL,
            [stop_loss_is_mandatory] bit NOT NULL,
            [maximum_slippage_fraction] decimal(9,8) NOT NULL,
            [time_in_force] varchar(10) NOT NULL,
            [trade_date] date NULL,
            [not_before_utc] datetime2(7) NULL,
            [new_entry_cutoff_utc] datetime2(7) NULL,
            [mandatory_exit_by_utc] datetime2(7) NULL,
            [allow_trailing_stop] bit NULL,
            [allow_break_even_move] bit NULL,
            [allow_time_exit] bit NULL,
            [allow_signal_exit] bit NULL,
            [exit_policy_version] varchar(50) NULL,
            [execution_policy_version] varchar(50) NOT NULL,
            [initial_status] varchar(20) NOT NULL,
            [status_reasons_json] nvarchar(max) NULL,
            [generated_at_utc] datetime2(7) NOT NULL,
            [valid_until_utc] datetime2(7) NOT NULL,
            [supersedes_trade_plan_uid] uniqueidentifier NULL,
            [is_current] bit NOT NULL
                CONSTRAINT [df_trade_plans_is_current] DEFAULT (1),
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [raw_contract_json] nvarchar(max) NOT NULL,
            [contract_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_trade_plans_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_trade_plans]
                PRIMARY KEY CLUSTERED ([trade_plan_id]),
            CONSTRAINT [uq_trade_plans_uid]
                UNIQUE ([trade_plan_uid]),
            CONSTRAINT [uq_trade_plans_message_uid]
                UNIQUE ([message_uid]),
            CONSTRAINT [uq_trade_plans_id_instrument]
                UNIQUE ([trade_plan_id], [instrument_id]),
            CONSTRAINT [uq_trade_plans_decision_version]
                UNIQUE ([risk_decision_id], [plan_version]),
            CONSTRAINT [fk_trade_plans_risk_decision]
                FOREIGN KEY ([risk_decision_id], [instrument_id])
                REFERENCES [risk].[risk_decisions] ([risk_decision_id], [instrument_id]),
            CONSTRAINT [fk_trade_plans_thesis]
                FOREIGN KEY ([thesis_id], [instrument_id])
                REFERENCES [thesis].[theses] ([thesis_id], [instrument_id]),
            CONSTRAINT [fk_trade_plans_signal]
                FOREIGN KEY ([signal_id], [instrument_id])
                REFERENCES [intelligence].[signals] ([signal_id], [instrument_id]),
            CONSTRAINT [fk_trade_plans_supersedes]
                FOREIGN KEY ([supersedes_trade_plan_uid])
                REFERENCES [risk].[trade_plans] ([trade_plan_uid]),
            CONSTRAINT [ck_trade_plans_contract_version]
                CHECK ([contract_version] = '1.0.0'),
            CONSTRAINT [ck_trade_plans_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_trade_plans_version]
                CHECK ([plan_version] >= 1),
            CONSTRAINT [ck_trade_plans_side]
                CHECK ([side] IN ('BUY', 'SELL')),
            CONSTRAINT [ck_trade_plans_position_intent]
                CHECK ([position_intent] IN ('INTRADAY', 'DELIVERY', 'CARRY_FORWARD')),
            CONSTRAINT [ck_trade_plans_entry_order_type]
                CHECK ([entry_order_type] IN ('MARKET', 'LIMIT', 'STOP_MARKET', 'STOP_LIMIT')),
            CONSTRAINT [ck_trade_plans_entry_prices]
                CHECK
                (
                    [entry_reference_price] > 0
                    AND ([entry_limit_price] IS NULL OR [entry_limit_price] > 0)
                    AND ([entry_trigger_price] IS NULL OR [entry_trigger_price] > 0)
                    AND ([minimum_acceptable_price] IS NULL OR [minimum_acceptable_price] > 0)
                    AND ([maximum_acceptable_price] IS NULL OR [maximum_acceptable_price] > 0)
                    AND ([minimum_acceptable_price] IS NULL
                        OR [entry_reference_price] >= [minimum_acceptable_price])
                    AND ([maximum_acceptable_price] IS NULL
                        OR [entry_reference_price] <= [maximum_acceptable_price])
                    AND ([minimum_acceptable_price] IS NULL
                        OR [maximum_acceptable_price] IS NULL
                        OR [maximum_acceptable_price] >= [minimum_acceptable_price])
                ),
            CONSTRAINT [ck_trade_plans_entry_order_fields]
                CHECK
                (
                    ([entry_order_type] = 'MARKET'
                        AND [entry_limit_price] IS NULL
                        AND [entry_trigger_price] IS NULL)
                    OR
                    ([entry_order_type] = 'LIMIT'
                        AND [entry_limit_price] IS NOT NULL
                        AND [entry_trigger_price] IS NULL)
                    OR
                    ([entry_order_type] = 'STOP_MARKET'
                        AND [entry_limit_price] IS NULL
                        AND [entry_trigger_price] IS NOT NULL)
                    OR
                    ([entry_order_type] = 'STOP_LIMIT'
                        AND [entry_limit_price] IS NOT NULL
                        AND [entry_trigger_price] IS NOT NULL)
                ),
            CONSTRAINT [ck_trade_plans_quantity]
                CHECK
                (
                    [approved_quantity] > 0
                    AND ([minimum_execution_quantity] IS NULL
                        OR [minimum_execution_quantity] BETWEEN 0 AND [approved_quantity])
                    AND ([allow_partial_fill] = 1
                        OR [minimum_execution_quantity] IS NULL
                        OR [minimum_execution_quantity] = [approved_quantity])
                ),
            CONSTRAINT [ck_trade_plans_stop_loss]
                CHECK
                (
                    [stop_loss_price] > 0
                    AND [stop_loss_is_mandatory] = 1
                    AND [stop_loss_order_type] IN ('STOP_MARKET', 'STOP_LIMIT', 'SYNTHETIC')
                    AND ([stop_loss_limit_price] IS NULL OR [stop_loss_limit_price] > 0)
                    AND ([stop_loss_order_type] <> 'STOP_LIMIT'
                        OR [stop_loss_limit_price] IS NOT NULL)
                ),
            CONSTRAINT [ck_trade_plans_direction]
                CHECK
                (
                    ([side] = 'BUY' AND [stop_loss_price] < [entry_reference_price])
                    OR
                    ([side] = 'SELL' AND [stop_loss_price] > [entry_reference_price])
                ),
            CONSTRAINT [ck_trade_plans_slippage]
                CHECK ([maximum_slippage_fraction] BETWEEN 0 AND 1),
            CONSTRAINT [ck_trade_plans_time_in_force]
                CHECK ([time_in_force] IN ('DAY', 'IOC')),
            CONSTRAINT [ck_trade_plans_session_times]
                CHECK
                (
                    ([not_before_utc] IS NULL
                        OR [new_entry_cutoff_utc] IS NULL
                        OR [new_entry_cutoff_utc] > [not_before_utc])
                    AND ([new_entry_cutoff_utc] IS NULL
                        OR [mandatory_exit_by_utc] IS NULL
                        OR [mandatory_exit_by_utc] >= [new_entry_cutoff_utc])
                ),
            CONSTRAINT [ck_trade_plans_status]
                CHECK
                (
                    [initial_status] IN
                    (
                        'CREATED', 'READY', 'REJECTED', 'EXPIRED',
                        'SUPERSEDED', 'SUBMITTED', 'COMPLETED', 'CANCELLED'
                    )
                ),
            CONSTRAINT [ck_trade_plans_validity]
                CHECK ([valid_until_utc] > [generated_at_utc]),
            CONSTRAINT [ck_trade_plans_version_lineage]
                CHECK
                (
                    ([plan_version] = 1 AND [supersedes_trade_plan_uid] IS NULL)
                    OR
                    ([plan_version] > 1 AND [supersedes_trade_plan_uid] IS NOT NULL)
                ),
            CONSTRAINT [ck_trade_plans_not_self_superseding]
                CHECK
                (
                    [supersedes_trade_plan_uid] IS NULL
                    OR [supersedes_trade_plan_uid] <> [trade_plan_uid]
                ),
            CONSTRAINT [ck_trade_plans_status_reasons_json]
                CHECK ([status_reasons_json] IS NULL OR ISJSON([status_reasons_json]) = 1),
            CONSTRAINT [ck_trade_plans_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_trade_plans_raw_contract_json]
                CHECK (ISJSON([raw_contract_json]) = 1),
            CONSTRAINT [ck_trade_plans_contract_hash]
                CHECK
                (
                    LEN(RTRIM([contract_hash])) = 64
                    AND [contract_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE UNIQUE INDEX [ux_trade_plans_current_decision]
            ON [risk].[trade_plans] ([risk_decision_id])
            WHERE [is_current] = 1;

        CREATE INDEX [ix_trade_plans_latest]
            ON [risk].[trade_plans]
            ([instrument_id], [generated_at_utc] DESC)
            INCLUDE
            (
                [risk_decision_id], [side], [approved_quantity], [stop_loss_price],
                [initial_status], [valid_until_utc], [is_current]
            );

        CREATE INDEX [ix_trade_plans_correlation]
            ON [risk].[trade_plans] ([correlation_id], [generated_at_utc]);
    END;

    IF OBJECT_ID(N'[risk].[trade_plan_targets]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[trade_plan_targets]
        (
            [trade_plan_target_id] bigint IDENTITY(1,1) NOT NULL,
            [trade_plan_id] bigint NOT NULL,
            [target_sequence] int NOT NULL,
            [target_price] decimal(19,6) NOT NULL,
            [quantity_fraction] decimal(9,8) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_trade_plan_targets_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_trade_plan_targets]
                PRIMARY KEY CLUSTERED ([trade_plan_target_id]),
            CONSTRAINT [uq_trade_plan_targets_sequence]
                UNIQUE ([trade_plan_id], [target_sequence]),
            CONSTRAINT [uq_trade_plan_targets_price]
                UNIQUE ([trade_plan_id], [target_price]),
            CONSTRAINT [fk_trade_plan_targets_plan]
                FOREIGN KEY ([trade_plan_id])
                REFERENCES [risk].[trade_plans] ([trade_plan_id]),
            CONSTRAINT [ck_trade_plan_targets_sequence]
                CHECK ([target_sequence] >= 1),
            CONSTRAINT [ck_trade_plan_targets_values]
                CHECK ([target_price] > 0 AND [quantity_fraction] > 0 AND [quantity_fraction] <= 1)
        );
    END;

    IF OBJECT_ID(N'[risk].[trade_plan_status_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[trade_plan_status_events]
        (
            [trade_plan_status_event_id] bigint IDENTITY(1,1) NOT NULL,
            [trade_plan_status_event_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_trade_plan_status_events_uid] DEFAULT NEWSEQUENTIALID(),
            [trade_plan_id] bigint NOT NULL,
            [event_sequence] int NOT NULL,
            [status] varchar(20) NOT NULL,
            [reason_codes_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_trade_plan_status_events_reasons] DEFAULT (N'[]'),
            [occurred_at_utc] datetime2(7) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_trade_plan_status_events_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_trade_plan_status_events]
                PRIMARY KEY CLUSTERED ([trade_plan_status_event_id]),
            CONSTRAINT [uq_trade_plan_status_events_uid]
                UNIQUE ([trade_plan_status_event_uid]),
            CONSTRAINT [uq_trade_plan_status_events_sequence]
                UNIQUE ([trade_plan_id], [event_sequence]),
            CONSTRAINT [fk_trade_plan_status_events_plan]
                FOREIGN KEY ([trade_plan_id])
                REFERENCES [risk].[trade_plans] ([trade_plan_id]),
            CONSTRAINT [ck_trade_plan_status_events_sequence]
                CHECK ([event_sequence] >= 0),
            CONSTRAINT [ck_trade_plan_status_events_status]
                CHECK
                (
                    [status] IN
                    (
                        'CREATED', 'READY', 'REJECTED', 'EXPIRED',
                        'SUPERSEDED', 'SUBMITTED', 'COMPLETED', 'CANCELLED'
                    )
                ),
            CONSTRAINT [ck_trade_plan_status_events_reasons_json]
                CHECK (ISJSON([reason_codes_json]) = 1),
            CONSTRAINT [ck_trade_plan_status_events_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE INDEX [ix_trade_plan_status_events_latest]
            ON [risk].[trade_plan_status_events]
            ([trade_plan_id], [event_sequence] DESC)
            INCLUDE ([status], [occurred_at_utc]);

        CREATE INDEX [ix_trade_plan_status_events_status]
            ON [risk].[trade_plan_status_events]
            ([status], [occurred_at_utc] DESC);
    END;

    UPDATE [operations].[database_metadata]
    SET
        [schema_baseline_version] = 'V0006',
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
