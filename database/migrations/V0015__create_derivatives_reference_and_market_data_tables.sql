/*
Migration: V0015__create_derivatives_reference_and_market_data_tables.sql
Purpose:
  Add effective-dated derivative contract metadata, expiry schedules, immutable
  futures-basis observations and normalized option-chain snapshots.
Dependencies:
  V0002__create_reference_tables.sql
  V0003__create_market_data_tables.sql
Backward compatibility:
  Fully additive. Existing cash, candle and quote workflows are unchanged.
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

    IF OBJECT_ID(N'[reference].[instruments]', N'U') IS NULL
        THROW 61501, 'V0002 reference.instruments is required.', 1;

    IF OBJECT_ID(N'[reference].[exchanges]', N'U') IS NULL
        THROW 61502, 'V0002 reference.exchanges is required.', 1;

    IF OBJECT_ID(N'[market].[data_sources]', N'U') IS NULL
        THROW 61503, 'V0003 market.data_sources is required.', 1;

    IF OBJECT_ID(N'[reference].[derivative_contracts]', N'U') IS NULL
    BEGIN
        CREATE TABLE [reference].[derivative_contracts]
        (
            [derivative_contract_id] bigint IDENTITY(1,1) NOT NULL,
            [derivative_contract_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_derivative_contracts_uid] DEFAULT NEWSEQUENTIALID(),
            [instrument_id] bigint NOT NULL,
            [underlying_instrument_id] bigint NOT NULL,
            [contract_class] varchar(30) NOT NULL,
            [expiry_date] date NOT NULL,
            [expiry_type] varchar(20) NOT NULL,
            [last_trading_date] date NOT NULL,
            [settlement_date] date NULL,
            [rollover_start_date] date NULL,
            [settlement_type] varchar(20) NOT NULL,
            [contract_multiplier] decimal(19,6) NOT NULL,
            [lot_size] decimal(19,6) NOT NULL,
            [strike_price] decimal(19,6) NULL,
            [option_type] varchar(10) NULL,
            [status] varchar(20) NOT NULL,
            [selection_eligible] bit NOT NULL
                CONSTRAINT [df_derivative_contracts_selection_eligible] DEFAULT (0),
            [valid_from_date] date NOT NULL,
            [valid_to_date] date NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_derivative_contracts_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_derivative_contracts_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_derivative_contracts]
                PRIMARY KEY CLUSTERED ([derivative_contract_id]),
            CONSTRAINT [uq_derivative_contracts_uid]
                UNIQUE ([derivative_contract_uid]),
            CONSTRAINT [uq_derivative_contracts_instrument_version]
                UNIQUE ([instrument_id], [valid_from_date]),
            CONSTRAINT [fk_derivative_contracts_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_derivative_contracts_underlying]
                FOREIGN KEY ([underlying_instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [ck_derivative_contracts_class]
                CHECK ([contract_class] IN
                    ('INDEX_FUTURE', 'STOCK_FUTURE', 'INDEX_OPTION', 'STOCK_OPTION')),
            CONSTRAINT [ck_derivative_contracts_expiry_type]
                CHECK ([expiry_type] IN
                    ('WEEKLY', 'MONTHLY', 'QUARTERLY', 'OTHER', 'UNKNOWN')),
            CONSTRAINT [ck_derivative_contracts_settlement_type]
                CHECK ([settlement_type] IN ('CASH', 'PHYSICAL', 'UNKNOWN')),
            CONSTRAINT [ck_derivative_contracts_status]
                CHECK ([status] IN ('ACTIVE', 'INACTIVE', 'EXPIRED')),
            CONSTRAINT [ck_derivative_contracts_dates]
                CHECK
                (
                    [last_trading_date] <= [expiry_date]
                    AND ([settlement_date] IS NULL OR [settlement_date] >= [last_trading_date])
                    AND ([rollover_start_date] IS NULL OR [rollover_start_date] <= [last_trading_date])
                    AND ([valid_to_date] IS NULL OR [valid_to_date] >= [valid_from_date])
                ),
            CONSTRAINT [ck_derivative_contracts_values]
                CHECK ([contract_multiplier] > 0 AND [lot_size] > 0),
            CONSTRAINT [ck_derivative_contracts_option_fields]
                CHECK
                (
                    ([contract_class] IN ('INDEX_OPTION', 'STOCK_OPTION')
                        AND [strike_price] IS NOT NULL AND [strike_price] > 0
                        AND [option_type] IN ('CALL', 'PUT'))
                    OR
                    ([contract_class] IN ('INDEX_FUTURE', 'STOCK_FUTURE')
                        AND [strike_price] IS NULL AND [option_type] IS NULL)
                ),
            CONSTRAINT [ck_derivative_contracts_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE UNIQUE INDEX [ux_derivative_contracts_current_instrument]
            ON [reference].[derivative_contracts] ([instrument_id])
            WHERE [valid_to_date] IS NULL;

        CREATE INDEX [ix_derivative_contracts_underlying_expiry]
            ON [reference].[derivative_contracts]
            ([underlying_instrument_id], [expiry_date], [contract_class], [status])
            INCLUDE
            ([instrument_id], [strike_price], [option_type], [lot_size],
             [selection_eligible], [valid_to_date]);
    END;

    IF OBJECT_ID(N'[reference].[derivative_expiry_schedules]', N'U') IS NULL
    BEGIN
        CREATE TABLE [reference].[derivative_expiry_schedules]
        (
            [derivative_expiry_schedule_id] bigint IDENTITY(1,1) NOT NULL,
            [derivative_expiry_schedule_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_derivative_expiry_schedules_uid] DEFAULT NEWSEQUENTIALID(),
            [exchange_id] bigint NOT NULL,
            [underlying_instrument_id] bigint NOT NULL,
            [market_segment] varchar(20) NOT NULL,
            [expiry_date] date NOT NULL,
            [expiry_type] varchar(20) NOT NULL,
            [last_trading_date] date NOT NULL,
            [settlement_date] date NULL,
            [rollover_start_date] date NULL,
            [status] varchar(20) NOT NULL,
            [calendar_version] varchar(100) NOT NULL,
            [valid_from_date] date NOT NULL,
            [valid_to_date] date NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_derivative_expiry_schedules_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_derivative_expiry_schedules_updated_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_derivative_expiry_schedules]
                PRIMARY KEY CLUSTERED ([derivative_expiry_schedule_id]),
            CONSTRAINT [uq_derivative_expiry_schedules_uid]
                UNIQUE ([derivative_expiry_schedule_uid]),
            CONSTRAINT [uq_derivative_expiry_schedules_version]
                UNIQUE
                ([underlying_instrument_id], [market_segment], [expiry_date], [calendar_version]),
            CONSTRAINT [fk_derivative_expiry_schedules_exchange]
                FOREIGN KEY ([exchange_id])
                REFERENCES [reference].[exchanges] ([exchange_id]),
            CONSTRAINT [fk_derivative_expiry_schedules_underlying]
                FOREIGN KEY ([underlying_instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [ck_derivative_expiry_schedules_segment]
                CHECK ([market_segment] IN ('FUTURES', 'OPTIONS')),
            CONSTRAINT [ck_derivative_expiry_schedules_type]
                CHECK ([expiry_type] IN
                    ('WEEKLY', 'MONTHLY', 'QUARTERLY', 'OTHER', 'UNKNOWN')),
            CONSTRAINT [ck_derivative_expiry_schedules_status]
                CHECK ([status] IN ('SCHEDULED', 'ACTIVE', 'EXPIRED', 'CANCELLED')),
            CONSTRAINT [ck_derivative_expiry_schedules_dates]
                CHECK
                (
                    [last_trading_date] <= [expiry_date]
                    AND ([settlement_date] IS NULL OR [settlement_date] >= [last_trading_date])
                    AND ([rollover_start_date] IS NULL OR [rollover_start_date] <= [last_trading_date])
                    AND ([valid_to_date] IS NULL OR [valid_to_date] >= [valid_from_date])
                ),
            CONSTRAINT [ck_derivative_expiry_schedules_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE UNIQUE INDEX [ux_derivative_expiry_schedules_current]
            ON [reference].[derivative_expiry_schedules]
            ([underlying_instrument_id], [market_segment], [expiry_date])
            WHERE [valid_to_date] IS NULL;

        CREATE INDEX [ix_derivative_expiry_schedules_lookup]
            ON [reference].[derivative_expiry_schedules]
            ([underlying_instrument_id], [market_segment], [expiry_date], [status])
            INCLUDE
            ([expiry_type], [last_trading_date], [settlement_date],
             [rollover_start_date], [calendar_version]);
    END;

    IF OBJECT_ID(N'[market].[futures_basis_observations]', N'U') IS NULL
    BEGIN
        CREATE TABLE [market].[futures_basis_observations]
        (
            [futures_basis_observation_id] bigint IDENTITY(1,1) NOT NULL,
            [futures_basis_observation_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_futures_basis_observations_uid] DEFAULT NEWSEQUENTIALID(),
            [data_source_id] bigint NOT NULL,
            [underlying_instrument_id] bigint NOT NULL,
            [future_instrument_id] bigint NOT NULL,
            [derivative_contract_id] bigint NOT NULL,
            [source_event_id] varchar(200) NOT NULL,
            [revision] int NOT NULL,
            [event_at_utc] datetime2(7) NOT NULL,
            [published_at_utc] datetime2(7) NULL,
            [received_at_utc] datetime2(7) NOT NULL,
            [underlying_price] decimal(19,6) NOT NULL,
            [future_price] decimal(19,6) NOT NULL,
            [basis_amount] decimal(19,6) NOT NULL,
            [basis_fraction] decimal(19,10) NOT NULL,
            [days_to_expiry] int NOT NULL,
            [annualized_basis_fraction] decimal(19,10) NULL,
            [quality_status] varchar(30) NOT NULL,
            [is_point_in_time_eligible] bit NOT NULL,
            [source_version] varchar(100) NOT NULL,
            [payload_hash] char(64) NOT NULL,
            [raw_payload_json] nvarchar(max) NULL,
            [correlation_id] uniqueidentifier NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_futures_basis_observations_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_futures_basis_observations]
                PRIMARY KEY CLUSTERED ([futures_basis_observation_id]),
            CONSTRAINT [uq_futures_basis_observations_uid]
                UNIQUE ([futures_basis_observation_uid]),
            CONSTRAINT [uq_futures_basis_observations_source]
                UNIQUE ([data_source_id], [source_event_id], [revision]),
            CONSTRAINT [fk_futures_basis_observations_source]
                FOREIGN KEY ([data_source_id])
                REFERENCES [market].[data_sources] ([data_source_id]),
            CONSTRAINT [fk_futures_basis_observations_underlying]
                FOREIGN KEY ([underlying_instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_futures_basis_observations_future]
                FOREIGN KEY ([future_instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_futures_basis_observations_contract]
                FOREIGN KEY ([derivative_contract_id])
                REFERENCES [reference].[derivative_contracts] ([derivative_contract_id]),
            CONSTRAINT [ck_futures_basis_observations_revision]
                CHECK ([revision] >= 0),
            CONSTRAINT [ck_futures_basis_observations_time]
                CHECK
                (
                    [received_at_utc] >= [event_at_utc]
                    AND ([published_at_utc] IS NULL OR [received_at_utc] >= [published_at_utc])
                ),
            CONSTRAINT [ck_futures_basis_observations_prices]
                CHECK
                (
                    [underlying_price] > 0
                    AND [future_price] > 0
                    AND [days_to_expiry] >= 0
                ),
            CONSTRAINT [ck_futures_basis_observations_quality]
                CHECK ([quality_status] IN
                    ('VALID', 'DEGRADED', 'STALE', 'INCOMPLETE', 'INVALID')),
            CONSTRAINT [ck_futures_basis_observations_payload_hash]
                CHECK
                (LEN(RTRIM([payload_hash])) = 64
                 AND [payload_hash] NOT LIKE '%[^0-9A-Fa-f]%'),
            CONSTRAINT [ck_futures_basis_observations_raw_json]
                CHECK ([raw_payload_json] IS NULL OR ISJSON([raw_payload_json]) = 1)
        );

        CREATE INDEX [ix_futures_basis_point_in_time]
            ON [market].[futures_basis_observations]
            ([future_instrument_id], [event_at_utc] DESC, [received_at_utc] DESC, [revision] DESC)
            INCLUDE
            ([underlying_instrument_id], [basis_amount], [basis_fraction],
             [annualized_basis_fraction], [quality_status], [is_point_in_time_eligible]);
    END;

    IF OBJECT_ID(N'[market].[option_chain_snapshots]', N'U') IS NULL
    BEGIN
        CREATE TABLE [market].[option_chain_snapshots]
        (
            [option_chain_snapshot_id] bigint IDENTITY(1,1) NOT NULL,
            [option_chain_snapshot_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_option_chain_snapshots_uid] DEFAULT NEWSEQUENTIALID(),
            [data_source_id] bigint NOT NULL,
            [underlying_instrument_id] bigint NOT NULL,
            [derivative_expiry_schedule_id] bigint NULL,
            [expiry_date] date NOT NULL,
            [source_event_id] varchar(200) NOT NULL,
            [revision] int NOT NULL,
            [event_at_utc] datetime2(7) NOT NULL,
            [published_at_utc] datetime2(7) NULL,
            [received_at_utc] datetime2(7) NOT NULL,
            [underlying_price] decimal(19,6) NOT NULL,
            [snapshot_status] varchar(20) NOT NULL,
            [quality_status] varchar(30) NOT NULL,
            [is_point_in_time_eligible] bit NOT NULL,
            [contract_count] int NOT NULL,
            [strike_count] int NOT NULL,
            [source_version] varchar(100) NOT NULL,
            [calculation_source_version] varchar(100) NULL,
            [payload_hash] char(64) NOT NULL,
            [raw_payload_json] nvarchar(max) NULL,
            [correlation_id] uniqueidentifier NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_option_chain_snapshots_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_option_chain_snapshots]
                PRIMARY KEY CLUSTERED ([option_chain_snapshot_id]),
            CONSTRAINT [uq_option_chain_snapshots_uid]
                UNIQUE ([option_chain_snapshot_uid]),
            CONSTRAINT [uq_option_chain_snapshots_source]
                UNIQUE ([data_source_id], [source_event_id], [revision]),
            CONSTRAINT [fk_option_chain_snapshots_source]
                FOREIGN KEY ([data_source_id])
                REFERENCES [market].[data_sources] ([data_source_id]),
            CONSTRAINT [fk_option_chain_snapshots_underlying]
                FOREIGN KEY ([underlying_instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_option_chain_snapshots_expiry_schedule]
                FOREIGN KEY ([derivative_expiry_schedule_id])
                REFERENCES [reference].[derivative_expiry_schedules]
                    ([derivative_expiry_schedule_id]),
            CONSTRAINT [ck_option_chain_snapshots_revision]
                CHECK ([revision] >= 0),
            CONSTRAINT [ck_option_chain_snapshots_time]
                CHECK
                (
                    [received_at_utc] >= [event_at_utc]
                    AND ([published_at_utc] IS NULL OR [received_at_utc] >= [published_at_utc])
                ),
            CONSTRAINT [ck_option_chain_snapshots_status]
                CHECK ([snapshot_status] IN ('COMPLETE', 'PARTIAL', 'INVALID')),
            CONSTRAINT [ck_option_chain_snapshots_quality]
                CHECK ([quality_status] IN
                    ('VALID', 'DEGRADED', 'STALE', 'INCOMPLETE', 'INVALID')),
            CONSTRAINT [ck_option_chain_snapshots_counts]
                CHECK ([contract_count] >= 0 AND [strike_count] >= 0),
            CONSTRAINT [ck_option_chain_snapshots_underlying_price]
                CHECK ([underlying_price] > 0),
            CONSTRAINT [ck_option_chain_snapshots_payload_hash]
                CHECK
                (LEN(RTRIM([payload_hash])) = 64
                 AND [payload_hash] NOT LIKE '%[^0-9A-Fa-f]%'),
            CONSTRAINT [ck_option_chain_snapshots_raw_json]
                CHECK ([raw_payload_json] IS NULL OR ISJSON([raw_payload_json]) = 1)
        );

        CREATE INDEX [ix_option_chain_snapshots_point_in_time]
            ON [market].[option_chain_snapshots]
            ([underlying_instrument_id], [expiry_date], [event_at_utc] DESC,
             [received_at_utc] DESC, [revision] DESC)
            INCLUDE
            ([snapshot_status], [quality_status], [is_point_in_time_eligible],
             [contract_count], [strike_count], [underlying_price]);
    END;

    IF OBJECT_ID(N'[market].[option_chain_entries]', N'U') IS NULL
    BEGIN
        CREATE TABLE [market].[option_chain_entries]
        (
            [option_chain_entry_id] bigint IDENTITY(1,1) NOT NULL,
            [option_chain_snapshot_id] bigint NOT NULL,
            [derivative_contract_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [quote_at_utc] datetime2(7) NOT NULL,
            [strike_price] decimal(19,6) NOT NULL,
            [option_type] varchar(10) NOT NULL,
            [bid_price] decimal(19,6) NULL,
            [ask_price] decimal(19,6) NULL,
            [last_price] decimal(19,6) NULL,
            [bid_quantity] decimal(19,6) NULL,
            [ask_quantity] decimal(19,6) NULL,
            [volume_quantity] decimal(19,6) NULL,
            [open_interest] decimal(19,6) NULL,
            [previous_open_interest] decimal(19,6) NULL,
            [open_interest_change] decimal(19,6) NULL,
            [implied_volatility] decimal(19,10) NULL,
            [delta] decimal(19,10) NULL,
            [gamma] decimal(19,10) NULL,
            [theta] decimal(19,10) NULL,
            [vega] decimal(19,10) NULL,
            [rho] decimal(19,10) NULL,
            [greeks_source_version] varchar(100) NULL,
            [quality_status] varchar(30) NOT NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_option_chain_entries_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_option_chain_entries]
                PRIMARY KEY CLUSTERED ([option_chain_entry_id]),
            CONSTRAINT [uq_option_chain_entries_contract]
                UNIQUE ([option_chain_snapshot_id], [derivative_contract_id]),
            CONSTRAINT [fk_option_chain_entries_snapshot]
                FOREIGN KEY ([option_chain_snapshot_id])
                REFERENCES [market].[option_chain_snapshots] ([option_chain_snapshot_id]),
            CONSTRAINT [fk_option_chain_entries_contract]
                FOREIGN KEY ([derivative_contract_id])
                REFERENCES [reference].[derivative_contracts] ([derivative_contract_id]),
            CONSTRAINT [fk_option_chain_entries_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [ck_option_chain_entries_option_type]
                CHECK ([option_type] IN ('CALL', 'PUT')),
            CONSTRAINT [ck_option_chain_entries_strike]
                CHECK ([strike_price] > 0),
            CONSTRAINT [ck_option_chain_entries_prices]
                CHECK
                (
                    ([bid_price] IS NULL OR [bid_price] >= 0)
                    AND ([ask_price] IS NULL OR [ask_price] >= 0)
                    AND ([last_price] IS NULL OR [last_price] >= 0)
                    AND ([bid_price] IS NULL OR [ask_price] IS NULL OR [ask_price] >= [bid_price])
                ),
            CONSTRAINT [ck_option_chain_entries_quantities]
                CHECK
                (
                    ([bid_quantity] IS NULL OR [bid_quantity] >= 0)
                    AND ([ask_quantity] IS NULL OR [ask_quantity] >= 0)
                    AND ([volume_quantity] IS NULL OR [volume_quantity] >= 0)
                    AND ([open_interest] IS NULL OR [open_interest] >= 0)
                    AND ([previous_open_interest] IS NULL OR [previous_open_interest] >= 0)
                ),
            CONSTRAINT [ck_option_chain_entries_iv]
                CHECK ([implied_volatility] IS NULL OR [implied_volatility] >= 0),
            CONSTRAINT [ck_option_chain_entries_delta]
                CHECK ([delta] IS NULL OR [delta] BETWEEN -1 AND 1),
            CONSTRAINT [ck_option_chain_entries_gamma]
                CHECK ([gamma] IS NULL OR [gamma] >= 0),
            CONSTRAINT [ck_option_chain_entries_quality]
                CHECK ([quality_status] IN
                    ('VALID', 'DEGRADED', 'STALE', 'INCOMPLETE', 'INVALID')),
            CONSTRAINT [ck_option_chain_entries_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE INDEX [ix_option_chain_entries_strike]
            ON [market].[option_chain_entries]
            ([option_chain_snapshot_id], [strike_price], [option_type])
            INCLUDE
            ([bid_price], [ask_price], [last_price], [volume_quantity],
             [open_interest], [implied_volatility], [delta], [gamma]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
