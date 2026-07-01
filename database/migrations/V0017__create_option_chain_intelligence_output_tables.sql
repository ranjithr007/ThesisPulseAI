/*
Migration: V0017__create_option_chain_intelligence_output_tables.sql
Purpose:
  Persist normalized Option-Chain Intelligence analytics and exact immutable
  option-chain snapshot lineage without granting signal or execution authority.
Dependencies:
  V0004__create_intelligence_and_signal_tables.sql
  V0015__create_derivatives_reference_and_market_data_tables.sql
Backward compatibility:
  Additive tables plus the OPTION_CHAIN value in the engine-output timeframe vocabulary.
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

    IF OBJECT_ID(N'[intelligence].[engine_outputs]', N'U') IS NULL
        THROW 61701, 'V0004 intelligence.engine_outputs is required.', 1;

    IF OBJECT_ID(N'[market].[option_chain_snapshots]', N'U') IS NULL
        THROW 61702, 'V0015 market.option_chain_snapshots is required.', 1;

    IF OBJECT_ID(N'[reference].[derivative_contracts]', N'U') IS NULL
        THROW 61703, 'V0015 reference.derivative_contracts is required.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE [name] = N'ck_engine_outputs_timeframe'
          AND [parent_object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
    )
    BEGIN
        ALTER TABLE [intelligence].[engine_outputs]
            DROP CONSTRAINT [ck_engine_outputs_timeframe];
    END;

    ALTER TABLE [intelligence].[engine_outputs] WITH CHECK
        ADD CONSTRAINT [ck_engine_outputs_timeframe]
        CHECK ([timeframe] IN ('1m', '5m', '15m', '1h', '1d', 'OPTION_CHAIN'));

    IF OBJECT_ID(N'[intelligence].[option_chain_output_snapshot_inputs]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[option_chain_output_snapshot_inputs]
        (
            [option_chain_output_snapshot_input_id] bigint IDENTITY(1,1) NOT NULL,
            [engine_output_id] bigint NOT NULL,
            [option_chain_snapshot_id] bigint NOT NULL,
            [input_role] varchar(30) NOT NULL,
            [input_sequence] int NOT NULL,
            [consumed_at_utc] datetime2(7) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_option_chain_output_snapshot_inputs_created_at]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_option_chain_output_snapshot_inputs]
                PRIMARY KEY CLUSTERED ([option_chain_output_snapshot_input_id]),
            CONSTRAINT [uq_option_chain_output_snapshot_inputs_snapshot]
                UNIQUE ([engine_output_id], [option_chain_snapshot_id]),
            CONSTRAINT [uq_option_chain_output_snapshot_inputs_sequence]
                UNIQUE ([engine_output_id], [input_sequence]),
            CONSTRAINT [fk_option_chain_output_snapshot_inputs_output]
                FOREIGN KEY ([engine_output_id])
                REFERENCES [intelligence].[engine_outputs] ([engine_output_id]),
            CONSTRAINT [fk_option_chain_output_snapshot_inputs_snapshot]
                FOREIGN KEY ([option_chain_snapshot_id])
                REFERENCES [market].[option_chain_snapshots] ([option_chain_snapshot_id]),
            CONSTRAINT [ck_option_chain_output_snapshot_inputs_role]
                CHECK ([input_role] IN ('PRIMARY', 'PRIOR', 'TERM_STRUCTURE')),
            CONSTRAINT [ck_option_chain_output_snapshot_inputs_sequence]
                CHECK ([input_sequence] >= 1)
        );

        CREATE INDEX [ix_option_chain_output_snapshot_inputs_output]
            ON [intelligence].[option_chain_output_snapshot_inputs]
            ([engine_output_id], [input_sequence])
            INCLUDE ([option_chain_snapshot_id], [input_role], [consumed_at_utc]);

        CREATE INDEX [ix_option_chain_output_snapshot_inputs_snapshot]
            ON [intelligence].[option_chain_output_snapshot_inputs]
            ([option_chain_snapshot_id], [engine_output_id]);
    END;

    IF OBJECT_ID(N'[intelligence].[option_chain_output_expiries]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[option_chain_output_expiries]
        (
            [option_chain_output_expiry_id] bigint IDENTITY(1,1) NOT NULL,
            [engine_output_id] bigint NOT NULL,
            [source_snapshot_uid] uniqueidentifier NOT NULL,
            [expiry_date] date NOT NULL,
            [underlying_price] decimal(19,6) NOT NULL,
            [call_open_interest] decimal(19,6) NOT NULL,
            [put_open_interest] decimal(19,6) NOT NULL,
            [pcr_open_interest] decimal(19,10) NULL,
            [call_volume] decimal(19,6) NOT NULL,
            [put_volume] decimal(19,6) NOT NULL,
            [pcr_volume] decimal(19,10) NULL,
            [max_pain_strike] decimal(19,6) NULL,
            [max_pain_distance_fraction] decimal(19,10) NULL,
            [max_pain_magnet_strength] decimal(9,8) NULL,
            [atm_call_implied_volatility] decimal(19,10) NULL,
            [atm_put_implied_volatility] decimal(19,10) NULL,
            [atm_put_call_skew] decimal(19,10) NULL,
            [rr25_skew] decimal(19,10) NULL,
            [accepted_contract_count] int NOT NULL,
            [accepted_strike_count] int NOT NULL,
            [component_coverage] decimal(9,8) NOT NULL,
            [warnings_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_option_chain_output_expiries_created_at]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_option_chain_output_expiries]
                PRIMARY KEY CLUSTERED ([option_chain_output_expiry_id]),
            CONSTRAINT [uq_option_chain_output_expiries_output]
                UNIQUE ([engine_output_id], [expiry_date]),
            CONSTRAINT [fk_option_chain_output_expiries_output]
                FOREIGN KEY ([engine_output_id])
                REFERENCES [intelligence].[engine_outputs] ([engine_output_id]),
            CONSTRAINT [fk_option_chain_output_expiries_snapshot_uid]
                FOREIGN KEY ([source_snapshot_uid])
                REFERENCES [market].[option_chain_snapshots] ([option_chain_snapshot_uid]),
            CONSTRAINT [ck_option_chain_output_expiries_values]
                CHECK
                (
                    [underlying_price] > 0
                    AND [call_open_interest] >= 0
                    AND [put_open_interest] >= 0
                    AND ([pcr_open_interest] IS NULL OR [pcr_open_interest] >= 0)
                    AND [call_volume] >= 0
                    AND [put_volume] >= 0
                    AND ([pcr_volume] IS NULL OR [pcr_volume] >= 0)
                    AND ([max_pain_strike] IS NULL OR [max_pain_strike] > 0)
                    AND ([max_pain_magnet_strength] IS NULL
                         OR [max_pain_magnet_strength] BETWEEN 0 AND 1)
                    AND ([atm_call_implied_volatility] IS NULL
                         OR [atm_call_implied_volatility] >= 0)
                    AND ([atm_put_implied_volatility] IS NULL
                         OR [atm_put_implied_volatility] >= 0)
                    AND [accepted_contract_count] >= 0
                    AND [accepted_strike_count] >= 0
                    AND [component_coverage] BETWEEN 0 AND 1
                ),
            CONSTRAINT [ck_option_chain_output_expiries_warnings_json]
                CHECK ([warnings_json] IS NULL OR ISJSON([warnings_json]) = 1)
        );

        CREATE INDEX [ix_option_chain_output_expiries_lookup]
            ON [intelligence].[option_chain_output_expiries]
            ([expiry_date], [engine_output_id])
            INCLUDE
            ([source_snapshot_uid], [pcr_open_interest], [pcr_volume],
             [max_pain_strike], [component_coverage]);
    END;

    IF OBJECT_ID(N'[intelligence].[option_chain_output_walls]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[option_chain_output_walls]
        (
            [option_chain_output_wall_id] bigint IDENTITY(1,1) NOT NULL,
            [option_chain_output_expiry_id] bigint NOT NULL,
            [option_type] varchar(10) NOT NULL,
            [wall_role] varchar(20) NOT NULL,
            [strike_price] decimal(19,6) NOT NULL,
            [open_interest] decimal(19,6) NOT NULL,
            [same_side_oi_share] decimal(9,8) NOT NULL,
            [wall_strength] decimal(9,8) NOT NULL,
            [distance_fraction] decimal(19,10) NOT NULL,
            [wall_rank] int NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_option_chain_output_walls_created_at]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_option_chain_output_walls]
                PRIMARY KEY CLUSTERED ([option_chain_output_wall_id]),
            CONSTRAINT [uq_option_chain_output_walls_rank]
                UNIQUE ([option_chain_output_expiry_id], [option_type], [wall_rank]),
            CONSTRAINT [fk_option_chain_output_walls_expiry]
                FOREIGN KEY ([option_chain_output_expiry_id])
                REFERENCES [intelligence].[option_chain_output_expiries]
                    ([option_chain_output_expiry_id]),
            CONSTRAINT [ck_option_chain_output_walls_type_role]
                CHECK
                (
                    ([option_type] = 'CALL' AND [wall_role] = 'RESISTANCE')
                    OR ([option_type] = 'PUT' AND [wall_role] = 'SUPPORT')
                ),
            CONSTRAINT [ck_option_chain_output_walls_values]
                CHECK
                (
                    [strike_price] > 0
                    AND [open_interest] >= 0
                    AND [same_side_oi_share] BETWEEN 0 AND 1
                    AND [wall_strength] BETWEEN 0 AND 1
                    AND [distance_fraction] >= 0
                    AND [wall_rank] >= 1
                )
        );
    END;

    IF OBJECT_ID(N'[intelligence].[option_chain_output_oi_flows]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[option_chain_output_oi_flows]
        (
            [option_chain_output_oi_flow_id] bigint IDENTITY(1,1) NOT NULL,
            [option_chain_output_expiry_id] bigint NOT NULL,
            [derivative_contract_uid] uniqueidentifier NOT NULL,
            [instrument_key] varchar(200) NOT NULL,
            [option_type] varchar(10) NOT NULL,
            [strike_price] decimal(19,6) NOT NULL,
            [previous_premium] decimal(19,6) NULL,
            [current_premium] decimal(19,6) NULL,
            [previous_open_interest] decimal(19,6) NULL,
            [current_open_interest] decimal(19,6) NULL,
            [premium_change_fraction] decimal(19,10) NULL,
            [open_interest_change_fraction] decimal(19,10) NULL,
            [flow_state] varchar(30) NOT NULL,
            [normalized_contribution] decimal(9,8) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_option_chain_output_oi_flows_created_at]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_option_chain_output_oi_flows]
                PRIMARY KEY CLUSTERED ([option_chain_output_oi_flow_id]),
            CONSTRAINT [uq_option_chain_output_oi_flows_contract]
                UNIQUE ([option_chain_output_expiry_id], [derivative_contract_uid]),
            CONSTRAINT [fk_option_chain_output_oi_flows_expiry]
                FOREIGN KEY ([option_chain_output_expiry_id])
                REFERENCES [intelligence].[option_chain_output_expiries]
                    ([option_chain_output_expiry_id]),
            CONSTRAINT [fk_option_chain_output_oi_flows_contract_uid]
                FOREIGN KEY ([derivative_contract_uid])
                REFERENCES [reference].[derivative_contracts] ([derivative_contract_uid]),
            CONSTRAINT [ck_option_chain_output_oi_flows_type]
                CHECK ([option_type] IN ('CALL', 'PUT')),
            CONSTRAINT [ck_option_chain_output_oi_flows_state]
                CHECK ([flow_state] IN
                    ('LONG_BUILDUP', 'SHORT_BUILDUP', 'SHORT_COVERING',
                     'LONG_UNWINDING', 'FLAT_OR_UNKNOWN')),
            CONSTRAINT [ck_option_chain_output_oi_flows_values]
                CHECK
                (
                    [strike_price] > 0
                    AND ([previous_premium] IS NULL OR [previous_premium] >= 0)
                    AND ([current_premium] IS NULL OR [current_premium] >= 0)
                    AND ([previous_open_interest] IS NULL OR [previous_open_interest] >= 0)
                    AND ([current_open_interest] IS NULL OR [current_open_interest] >= 0)
                    AND [normalized_contribution] BETWEEN -1 AND 1
                )
        );

        CREATE INDEX [ix_option_chain_output_oi_flows_state]
            ON [intelligence].[option_chain_output_oi_flows]
            ([option_chain_output_expiry_id], [flow_state], [option_type])
            INCLUDE ([strike_price], [current_open_interest], [normalized_contribution]);
    END;

    IF OBJECT_ID(N'[intelligence].[option_chain_output_max_pain_points]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[option_chain_output_max_pain_points]
        (
            [option_chain_output_max_pain_point_id] bigint IDENTITY(1,1) NOT NULL,
            [option_chain_output_expiry_id] bigint NOT NULL,
            [settlement_strike] decimal(19,6) NOT NULL,
            [call_payout] decimal(38,6) NOT NULL,
            [put_payout] decimal(38,6) NOT NULL,
            [total_payout] decimal(38,6) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_option_chain_output_max_pain_created_at]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_option_chain_output_max_pain_points]
                PRIMARY KEY CLUSTERED ([option_chain_output_max_pain_point_id]),
            CONSTRAINT [uq_option_chain_output_max_pain_strike]
                UNIQUE ([option_chain_output_expiry_id], [settlement_strike]),
            CONSTRAINT [fk_option_chain_output_max_pain_expiry]
                FOREIGN KEY ([option_chain_output_expiry_id])
                REFERENCES [intelligence].[option_chain_output_expiries]
                    ([option_chain_output_expiry_id]),
            CONSTRAINT [ck_option_chain_output_max_pain_values]
                CHECK
                (
                    [settlement_strike] > 0
                    AND [call_payout] >= 0
                    AND [put_payout] >= 0
                    AND [total_payout] >= 0
                    AND [total_payout] = [call_payout] + [put_payout]
                )
        );
    END;

    IF OBJECT_ID(N'[intelligence].[option_chain_output_iv_term_points]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[option_chain_output_iv_term_points]
        (
            [option_chain_output_iv_term_point_id] bigint IDENTITY(1,1) NOT NULL,
            [engine_output_id] bigint NOT NULL,
            [source_snapshot_uid] uniqueidentifier NOT NULL,
            [expiry_date] date NOT NULL,
            [days_to_expiry] int NOT NULL,
            [atm_strike_price] decimal(19,6) NOT NULL,
            [call_implied_volatility] decimal(19,10) NOT NULL,
            [put_implied_volatility] decimal(19,10) NOT NULL,
            [atm_implied_volatility] decimal(19,10) NOT NULL,
            [pair_method] varchar(30) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_option_chain_output_iv_term_created_at]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_option_chain_output_iv_term_points]
                PRIMARY KEY CLUSTERED ([option_chain_output_iv_term_point_id]),
            CONSTRAINT [uq_option_chain_output_iv_term_expiry]
                UNIQUE ([engine_output_id], [expiry_date]),
            CONSTRAINT [fk_option_chain_output_iv_term_output]
                FOREIGN KEY ([engine_output_id])
                REFERENCES [intelligence].[engine_outputs] ([engine_output_id]),
            CONSTRAINT [fk_option_chain_output_iv_term_snapshot_uid]
                FOREIGN KEY ([source_snapshot_uid])
                REFERENCES [market].[option_chain_snapshots] ([option_chain_snapshot_uid]),
            CONSTRAINT [ck_option_chain_output_iv_term_method]
                CHECK ([pair_method] IN ('EXACT_ATM', 'NEAREST_MATCHED_PAIR')),
            CONSTRAINT [ck_option_chain_output_iv_term_values]
                CHECK
                (
                    [days_to_expiry] >= 0
                    AND [atm_strike_price] > 0
                    AND [call_implied_volatility] >= 0
                    AND [put_implied_volatility] >= 0
                    AND [atm_implied_volatility] >= 0
                )
        );

        CREATE INDEX [ix_option_chain_output_iv_term_curve]
            ON [intelligence].[option_chain_output_iv_term_points]
            ([engine_output_id], [days_to_expiry], [expiry_date])
            INCLUDE
            ([atm_strike_price], [atm_implied_volatility],
             [call_implied_volatility], [put_implied_volatility]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
