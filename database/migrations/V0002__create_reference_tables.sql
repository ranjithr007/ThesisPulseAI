/*
Migration: V0002__create_reference_tables.sql
Purpose:
  Create versioned reference structures for exchanges, calendars, sessions,
  instruments, universes, brokers and broker instrument mappings.
Dependencies:
  V0001__create_schemas_and_migration_metadata.sql
Expected runtime impact:
  Additive metadata DDL. No market or execution data is scanned.
Locking considerations:
  Schema modification locks are acquired while tables, constraints and indexes are created.
Backward-compatibility window:
  Fully additive.
Data migration requirements:
  None. Reference seed data is intentionally separate.
Verification script:
  database/verification/V0002__verify_reference_tables.sql
Recovery plan:
  Roll forward with a later migration. Destructive rollback is limited to disposable local databases.
*/

-- SQL Server requires these session options when filtered indexes are created
-- and when rows covered by those indexes are later modified.
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

    IF SCHEMA_ID(N'reference') IS NULL
        THROW 52001, 'V0001 is required: schema reference does not exist.', 1;

    IF OBJECT_ID(N'[reference].[exchanges]', N'U') IS NULL
    BEGIN
        CREATE TABLE [reference].[exchanges]
        (
            [exchange_id] bigint IDENTITY(1,1) NOT NULL,
            [exchange_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_exchanges_exchange_uid] DEFAULT NEWSEQUENTIALID(),
            [exchange_code] varchar(20) NOT NULL,
            [exchange_name] nvarchar(200) NOT NULL,
            [country_code] char(2) NOT NULL,
            [timezone_id] varchar(100) NOT NULL,
            [currency_code] char(3) NOT NULL,
            [is_active] bit NOT NULL
                CONSTRAINT [df_exchanges_is_active] DEFAULT (1),
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_exchanges_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_exchanges_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_exchanges] PRIMARY KEY CLUSTERED ([exchange_id]),
            CONSTRAINT [uq_exchanges_uid] UNIQUE ([exchange_uid]),
            CONSTRAINT [uq_exchanges_code] UNIQUE ([exchange_code]),
            CONSTRAINT [ck_exchanges_code] CHECK (LEN([exchange_code]) BETWEEN 2 AND 20),
            CONSTRAINT [ck_exchanges_country_code] CHECK (LEN(RTRIM([country_code])) = 2),
            CONSTRAINT [ck_exchanges_currency_code] CHECK (LEN(RTRIM([currency_code])) = 3)
        );
    END;

    IF OBJECT_ID(N'[reference].[exchange_calendars]', N'U') IS NULL
    BEGIN
        CREATE TABLE [reference].[exchange_calendars]
        (
            [exchange_calendar_id] bigint IDENTITY(1,1) NOT NULL,
            [exchange_id] bigint NOT NULL,
            [calendar_version] varchar(50) NOT NULL,
            [timezone_id] varchar(100) NOT NULL,
            [valid_from_date] date NOT NULL,
            [valid_to_date] date NULL,
            [status] varchar(20) NOT NULL,
            [description] nvarchar(500) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_exchange_calendars_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_exchange_calendars_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_exchange_calendars] PRIMARY KEY CLUSTERED ([exchange_calendar_id]),
            CONSTRAINT [fk_exchange_calendars_exchange]
                FOREIGN KEY ([exchange_id]) REFERENCES [reference].[exchanges] ([exchange_id]),
            CONSTRAINT [uq_exchange_calendars_version]
                UNIQUE ([exchange_id], [calendar_version]),
            CONSTRAINT [ck_exchange_calendars_status]
                CHECK ([status] IN ('DRAFT', 'ACTIVE', 'RETIRED')),
            CONSTRAINT [ck_exchange_calendars_dates]
                CHECK ([valid_to_date] IS NULL OR [valid_to_date] >= [valid_from_date])
        );

        CREATE UNIQUE INDEX [ux_exchange_calendars_active]
            ON [reference].[exchange_calendars] ([exchange_id])
            WHERE [status] = 'ACTIVE';

        CREATE INDEX [ix_exchange_calendars_validity]
            ON [reference].[exchange_calendars]
            ([exchange_id], [valid_from_date], [valid_to_date]);
    END;

    IF OBJECT_ID(N'[reference].[calendar_days]', N'U') IS NULL
    BEGIN
        CREATE TABLE [reference].[calendar_days]
        (
            [calendar_day_id] bigint IDENTITY(1,1) NOT NULL,
            [exchange_calendar_id] bigint NOT NULL,
            [trade_date] date NOT NULL,
            [day_type] varchar(30) NOT NULL,
            [is_trading_day] bit NOT NULL,
            [holiday_name] nvarchar(200) NULL,
            [settlement_date] date NULL,
            [notes] nvarchar(500) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_calendar_days_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_calendar_days] PRIMARY KEY CLUSTERED ([calendar_day_id]),
            CONSTRAINT [fk_calendar_days_calendar]
                FOREIGN KEY ([exchange_calendar_id])
                REFERENCES [reference].[exchange_calendars] ([exchange_calendar_id]),
            CONSTRAINT [uq_calendar_days_date]
                UNIQUE ([exchange_calendar_id], [trade_date]),
            CONSTRAINT [ck_calendar_days_type]
                CHECK ([day_type] IN ('TRADING', 'HOLIDAY', 'SPECIAL_SESSION')),
            CONSTRAINT [ck_calendar_days_trading_flag]
                CHECK
                (
                    ([day_type] = 'HOLIDAY' AND [is_trading_day] = 0)
                    OR
                    ([day_type] IN ('TRADING', 'SPECIAL_SESSION') AND [is_trading_day] = 1)
                ),
            CONSTRAINT [ck_calendar_days_holiday_name]
                CHECK ([day_type] <> 'HOLIDAY' OR [holiday_name] IS NOT NULL)
        );

        CREATE INDEX [ix_calendar_days_trading_date]
            ON [reference].[calendar_days] ([trade_date], [is_trading_day]);
    END;

    IF OBJECT_ID(N'[reference].[trading_sessions]', N'U') IS NULL
    BEGIN
        CREATE TABLE [reference].[trading_sessions]
        (
            [trading_session_id] bigint IDENTITY(1,1) NOT NULL,
            [exchange_calendar_id] bigint NOT NULL,
            [market_segment] varchar(30) NOT NULL,
            [session_code] varchar(30) NOT NULL,
            [valid_from_date] date NOT NULL,
            [valid_to_date] date NULL,
            [start_time_local] time(7) NOT NULL,
            [end_time_local] time(7) NOT NULL,
            [crosses_midnight] bit NOT NULL
                CONSTRAINT [df_trading_sessions_crosses_midnight] DEFAULT (0),
            [is_order_entry_allowed] bit NOT NULL
                CONSTRAINT [df_trading_sessions_order_entry] DEFAULT (1),
            [is_market_data_expected] bit NOT NULL
                CONSTRAINT [df_trading_sessions_market_data] DEFAULT (1),
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_trading_sessions_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_trading_sessions_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_trading_sessions] PRIMARY KEY CLUSTERED ([trading_session_id]),
            CONSTRAINT [fk_trading_sessions_calendar]
                FOREIGN KEY ([exchange_calendar_id])
                REFERENCES [reference].[exchange_calendars] ([exchange_calendar_id]),
            CONSTRAINT [uq_trading_sessions_version]
                UNIQUE
                (
                    [exchange_calendar_id],
                    [market_segment],
                    [session_code],
                    [valid_from_date]
                ),
            CONSTRAINT [ck_trading_sessions_segment]
                CHECK ([market_segment] IN ('ALL', 'CASH', 'INDEX', 'FUTURES', 'OPTIONS')),
            CONSTRAINT [ck_trading_sessions_code]
                CHECK ([session_code] IN ('PRE_OPEN', 'REGULAR', 'CLOSING', 'AFTER_MARKET', 'SPECIAL')),
            CONSTRAINT [ck_trading_sessions_dates]
                CHECK ([valid_to_date] IS NULL OR [valid_to_date] >= [valid_from_date]),
            CONSTRAINT [ck_trading_sessions_times]
                CHECK
                (
                    ([crosses_midnight] = 0 AND [end_time_local] > [start_time_local])
                    OR
                    ([crosses_midnight] = 1 AND [end_time_local] <= [start_time_local])
                )
        );

        CREATE INDEX [ix_trading_sessions_lookup]
            ON [reference].[trading_sessions]
            ([exchange_calendar_id], [market_segment], [valid_from_date], [valid_to_date]);
    END;

    IF OBJECT_ID(N'[reference].[instruments]', N'U') IS NULL
    BEGIN
        CREATE TABLE [reference].[instruments]
        (
            [instrument_id] bigint IDENTITY(1,1) NOT NULL,
            [instrument_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_instruments_instrument_uid] DEFAULT NEWSEQUENTIALID(),
            [exchange_id] bigint NOT NULL,
            [canonical_symbol] varchar(100) NOT NULL,
            [display_name] nvarchar(200) NOT NULL,
            [instrument_type] varchar(30) NOT NULL,
            [market_segment] varchar(30) NOT NULL,
            [base_currency_code] char(3) NOT NULL,
            [tick_size] decimal(19,6) NOT NULL,
            [lot_size] decimal(19,6) NOT NULL,
            [price_scale] smallint NOT NULL,
            [quantity_scale] smallint NOT NULL,
            [underlying_instrument_id] bigint NULL,
            [expiry_date] date NULL,
            [strike_price] decimal(19,6) NULL,
            [option_type] varchar(10) NULL,
            [status] varchar(20) NOT NULL,
            [valid_from_date] date NOT NULL,
            [valid_to_date] date NULL,
            [is_trade_allowed] bit NOT NULL
                CONSTRAINT [df_instruments_trade_allowed] DEFAULT (0),
            [is_short_allowed] bit NOT NULL
                CONSTRAINT [df_instruments_short_allowed] DEFAULT (0),
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_instruments_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_instruments_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_instruments] PRIMARY KEY CLUSTERED ([instrument_id]),
            CONSTRAINT [uq_instruments_uid] UNIQUE ([instrument_uid]),
            CONSTRAINT [uq_instruments_symbol_version]
                UNIQUE ([exchange_id], [canonical_symbol], [valid_from_date]),
            CONSTRAINT [fk_instruments_exchange]
                FOREIGN KEY ([exchange_id]) REFERENCES [reference].[exchanges] ([exchange_id]),
            CONSTRAINT [fk_instruments_underlying]
                FOREIGN KEY ([underlying_instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [ck_instruments_type]
                CHECK ([instrument_type] IN ('EQUITY', 'INDEX', 'ETF', 'FUTURE', 'OPTION', 'CURRENCY', 'COMMODITY')),
            CONSTRAINT [ck_instruments_segment]
                CHECK ([market_segment] IN ('CASH', 'INDEX', 'FUTURES', 'OPTIONS')),
            CONSTRAINT [ck_instruments_status]
                CHECK ([status] IN ('ACTIVE', 'INACTIVE', 'SUSPENDED', 'DELISTED', 'EXPIRED')),
            CONSTRAINT [ck_instruments_option_type]
                CHECK ([option_type] IS NULL OR [option_type] IN ('CALL', 'PUT')),
            CONSTRAINT [ck_instruments_tick_size] CHECK ([tick_size] > 0),
            CONSTRAINT [ck_instruments_lot_size] CHECK ([lot_size] > 0),
            CONSTRAINT [ck_instruments_scales]
                CHECK ([price_scale] BETWEEN 0 AND 10 AND [quantity_scale] BETWEEN 0 AND 10),
            CONSTRAINT [ck_instruments_dates]
                CHECK ([valid_to_date] IS NULL OR [valid_to_date] >= [valid_from_date]),
            CONSTRAINT [ck_instruments_derivative_fields]
                CHECK
                (
                    ([instrument_type] = 'OPTION'
                        AND [underlying_instrument_id] IS NOT NULL
                        AND [expiry_date] IS NOT NULL
                        AND [strike_price] IS NOT NULL
                        AND [strike_price] > 0
                        AND [option_type] IS NOT NULL)
                    OR
                    ([instrument_type] = 'FUTURE'
                        AND [underlying_instrument_id] IS NOT NULL
                        AND [expiry_date] IS NOT NULL
                        AND [strike_price] IS NULL
                        AND [option_type] IS NULL)
                    OR
                    ([instrument_type] NOT IN ('OPTION', 'FUTURE')
                        AND [strike_price] IS NULL
                        AND [option_type] IS NULL)
                )
        );

        CREATE INDEX [ix_instruments_active_symbol]
            ON [reference].[instruments]
            ([exchange_id], [canonical_symbol], [status])
            INCLUDE ([instrument_uid], [instrument_type], [market_segment], [is_trade_allowed]);

        CREATE INDEX [ix_instruments_underlying_expiry]
            ON [reference].[instruments]
            ([underlying_instrument_id], [expiry_date], [instrument_type])
            WHERE [underlying_instrument_id] IS NOT NULL;
    END;

    IF OBJECT_ID(N'[reference].[universe_versions]', N'U') IS NULL
    BEGIN
        CREATE TABLE [reference].[universe_versions]
        (
            [universe_version_id] bigint IDENTITY(1,1) NOT NULL,
            [universe_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_universe_versions_uid] DEFAULT NEWSEQUENTIALID(),
            [universe_code] varchar(50) NOT NULL,
            [universe_version] varchar(50) NOT NULL,
            [environment] varchar(30) NOT NULL,
            [status] varchar(20) NOT NULL,
            [valid_from_date] date NOT NULL,
            [valid_to_date] date NULL,
            [description] nvarchar(1000) NULL,
            [approved_at_utc] datetime2(7) NULL,
            [approved_by] nvarchar(256) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_universe_versions_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_universe_versions_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_universe_versions] PRIMARY KEY CLUSTERED ([universe_version_id]),
            CONSTRAINT [uq_universe_versions_uid] UNIQUE ([universe_uid]),
            CONSTRAINT [uq_universe_versions_code_version]
                UNIQUE ([universe_code], [universe_version], [environment]),
            CONSTRAINT [ck_universe_versions_environment]
                CHECK ([environment] IN ('RESEARCH', 'PAPER', 'SHADOW', 'RESTRICTED_LIVE', 'LIVE')),
            CONSTRAINT [ck_universe_versions_status]
                CHECK ([status] IN ('DRAFT', 'APPROVED', 'ACTIVE', 'RETIRED')),
            CONSTRAINT [ck_universe_versions_dates]
                CHECK ([valid_to_date] IS NULL OR [valid_to_date] >= [valid_from_date]),
            CONSTRAINT [ck_universe_versions_approval]
                CHECK
                (
                    ([status] = 'DRAFT' AND [approved_at_utc] IS NULL AND [approved_by] IS NULL)
                    OR
                    ([status] <> 'DRAFT' AND [approved_at_utc] IS NOT NULL AND [approved_by] IS NOT NULL)
                )
        );

        CREATE UNIQUE INDEX [ux_universe_versions_active]
            ON [reference].[universe_versions] ([universe_code], [environment])
            WHERE [status] = 'ACTIVE';
    END;

    IF OBJECT_ID(N'[reference].[universe_members]', N'U') IS NULL
    BEGIN
        CREATE TABLE [reference].[universe_members]
        (
            [universe_member_id] bigint IDENTITY(1,1) NOT NULL,
            [universe_version_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [member_role] varchar(20) NOT NULL,
            [allocation_weight] decimal(12,8) NULL,
            [is_trade_allowed] bit NOT NULL,
            [is_short_allowed] bit NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_universe_members_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_universe_members] PRIMARY KEY CLUSTERED ([universe_member_id]),
            CONSTRAINT [fk_universe_members_version]
                FOREIGN KEY ([universe_version_id])
                REFERENCES [reference].[universe_versions] ([universe_version_id]),
            CONSTRAINT [fk_universe_members_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [uq_universe_members_version_instrument]
                UNIQUE ([universe_version_id], [instrument_id]),
            CONSTRAINT [ck_universe_members_role]
                CHECK ([member_role] IN ('PRIMARY', 'CONTEXT', 'HEDGE', 'BENCHMARK')),
            CONSTRAINT [ck_universe_members_weight]
                CHECK ([allocation_weight] IS NULL OR [allocation_weight] BETWEEN 0 AND 1),
            CONSTRAINT [ck_universe_members_short]
                CHECK ([is_short_allowed] = 0 OR [is_trade_allowed] = 1)
        );

        CREATE INDEX [ix_universe_members_instrument]
            ON [reference].[universe_members]
            ([instrument_id], [universe_version_id]);
    END;

    IF OBJECT_ID(N'[reference].[brokers]', N'U') IS NULL
    BEGIN
        CREATE TABLE [reference].[brokers]
        (
            [broker_id] bigint IDENTITY(1,1) NOT NULL,
            [broker_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_brokers_uid] DEFAULT NEWSEQUENTIALID(),
            [broker_code] varchar(30) NOT NULL,
            [broker_name] nvarchar(200) NOT NULL,
            [is_active] bit NOT NULL
                CONSTRAINT [df_brokers_is_active] DEFAULT (1),
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_brokers_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_brokers_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_brokers] PRIMARY KEY CLUSTERED ([broker_id]),
            CONSTRAINT [uq_brokers_uid] UNIQUE ([broker_uid]),
            CONSTRAINT [uq_brokers_code] UNIQUE ([broker_code])
        );
    END;

    IF OBJECT_ID(N'[reference].[broker_instrument_mappings]', N'U') IS NULL
    BEGIN
        CREATE TABLE [reference].[broker_instrument_mappings]
        (
            [broker_instrument_mapping_id] bigint IDENTITY(1,1) NOT NULL,
            [broker_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [broker_instrument_key] varchar(200) NOT NULL,
            [broker_symbol] varchar(100) NOT NULL,
            [broker_exchange_code] varchar(50) NOT NULL,
            [broker_segment] varchar(50) NOT NULL,
            [valid_from_date] date NOT NULL,
            [valid_to_date] date NULL,
            [is_active] bit NOT NULL
                CONSTRAINT [df_broker_instrument_mappings_active] DEFAULT (1),
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_broker_instrument_mappings_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_broker_instrument_mappings_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_broker_instrument_mappings]
                PRIMARY KEY CLUSTERED ([broker_instrument_mapping_id]),
            CONSTRAINT [fk_broker_instrument_mappings_broker]
                FOREIGN KEY ([broker_id]) REFERENCES [reference].[brokers] ([broker_id]),
            CONSTRAINT [fk_broker_instrument_mappings_instrument]
                FOREIGN KEY ([instrument_id]) REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [uq_broker_instrument_mappings_key_version]
                UNIQUE ([broker_id], [broker_instrument_key], [valid_from_date]),
            CONSTRAINT [uq_broker_instrument_mappings_instrument_version]
                UNIQUE ([broker_id], [instrument_id], [valid_from_date]),
            CONSTRAINT [ck_broker_instrument_mappings_dates]
                CHECK ([valid_to_date] IS NULL OR [valid_to_date] >= [valid_from_date]),
            CONSTRAINT [ck_broker_instrument_mappings_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE UNIQUE INDEX [ux_broker_instrument_mappings_open_instrument]
            ON [reference].[broker_instrument_mappings] ([broker_id], [instrument_id])
            WHERE [is_active] = 1 AND [valid_to_date] IS NULL;

        CREATE UNIQUE INDEX [ux_broker_instrument_mappings_open_key]
            ON [reference].[broker_instrument_mappings] ([broker_id], [broker_instrument_key])
            WHERE [is_active] = 1 AND [valid_to_date] IS NULL;

        CREATE INDEX [ix_broker_instrument_mappings_lookup]
            ON [reference].[broker_instrument_mappings]
            ([broker_id], [broker_symbol], [broker_exchange_code], [broker_segment]);
    END;

    UPDATE [operations].[database_metadata]
    SET
        [schema_baseline_version] = 'V0002',
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
