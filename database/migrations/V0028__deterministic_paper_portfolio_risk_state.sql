/*
Migration: V0028__deterministic_paper_portfolio_risk_state.sql
Purpose:
  Project immutable PAPER P&L snapshots into Risk-owned portfolio snapshots and
  durable portfolio operating state.
Authority boundary:
  Risk Service may restrict or block new exposure. This migration does not add
  liquidation, exit-order, broker, SHADOW or LIVE authority.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'[risk].[risk_policies]', N'U') IS NULL
        THROW 59801, 'V0006 is required: risk.risk_policies does not exist.', 1;
    IF OBJECT_ID(N'[risk].[portfolio_snapshots]', N'U') IS NULL
        THROW 59802, 'V0006 is required: risk.portfolio_snapshots does not exist.', 1;
    IF OBJECT_ID(N'[portfolio].[pnl_snapshots]', N'U') IS NULL
        THROW 59803, 'V0008 is required: portfolio.pnl_snapshots does not exist.', 1;
    IF OBJECT_ID(N'[portfolio].[valuation_work_items]', N'U') IS NULL
        THROW 59804, 'V0027 is required: portfolio.valuation_work_items does not exist.', 1;

    IF OBJECT_ID(N'[risk].[portfolio_risk_states]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[portfolio_risk_states]
        (
            [portfolio_risk_state_id] bigint IDENTITY(1,1) NOT NULL,
            [portfolio_risk_state_uid] uniqueidentifier NOT NULL,
            [portfolio_id] bigint NOT NULL,
            [portfolio_code] varchar(100) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [risk_policy_id] bigint NOT NULL,
            [latest_portfolio_snapshot_id] bigint NOT NULL,
            [latest_source_pnl_snapshot_id] bigint NOT NULL,
            [current_operating_mode] varchar(20) NOT NULL,
            [allows_new_exposure] bit NOT NULL,
            [allows_risk_reducing_exits] bit NOT NULL,
            [risk_multiplier] decimal(9,8) NOT NULL,
            [maximum_concurrent_new_positions] int NOT NULL,
            [equity_amount] decimal(19,6) NOT NULL,
            [daily_pnl_amount] decimal(19,6) NOT NULL,
            [weekly_pnl_amount] decimal(19,6) NOT NULL,
            [daily_loss_fraction] decimal(9,8) NOT NULL,
            [weekly_loss_fraction] decimal(9,8) NOT NULL,
            [strategy_drawdown_fraction] decimal(9,8) NOT NULL,
            [portfolio_drawdown_fraction] decimal(9,8) NOT NULL,
            [reason_codes_json] nvarchar(max) NOT NULL,
            [current_state_version] int NOT NULL,
            [as_of_utc] datetime2(7) NOT NULL,
            [evaluated_at_utc] datetime2(7) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_risk_states_created_at] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_risk_states_updated_at] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_portfolio_risk_states]
                PRIMARY KEY CLUSTERED ([portfolio_risk_state_id]),
            CONSTRAINT [uq_portfolio_risk_states_uid]
                UNIQUE ([portfolio_risk_state_uid]),
            CONSTRAINT [uq_portfolio_risk_states_scope]
                UNIQUE ([portfolio_id], [environment]),
            CONSTRAINT [fk_portfolio_risk_states_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [fk_portfolio_risk_states_policy]
                FOREIGN KEY ([risk_policy_id])
                REFERENCES [risk].[risk_policies] ([risk_policy_id]),
            CONSTRAINT [fk_portfolio_risk_states_snapshot]
                FOREIGN KEY ([latest_portfolio_snapshot_id])
                REFERENCES [risk].[portfolio_snapshots] ([portfolio_snapshot_id]),
            CONSTRAINT [fk_portfolio_risk_states_source]
                FOREIGN KEY ([latest_source_pnl_snapshot_id])
                REFERENCES [portfolio].[pnl_snapshots] ([pnl_snapshot_id]),
            CONSTRAINT [ck_portfolio_risk_states_environment]
                CHECK ([environment] = 'PAPER'),
            CONSTRAINT [ck_portfolio_risk_states_mode]
                CHECK ([current_operating_mode] IN ('NORMAL','RESTRICTED','CLOSE_ONLY','PAUSED','HALTED')),
            CONSTRAINT [ck_portfolio_risk_states_values]
                CHECK
                (
                    [risk_multiplier] BETWEEN 0 AND 1
                    AND [maximum_concurrent_new_positions] >= 0
                    AND [equity_amount] >= 0
                    AND [daily_loss_fraction] BETWEEN 0 AND 1
                    AND [weekly_loss_fraction] BETWEEN 0 AND 1
                    AND [strategy_drawdown_fraction] BETWEEN 0 AND 1
                    AND [portfolio_drawdown_fraction] BETWEEN 0 AND 1
                    AND [current_state_version] >= 1
                ),
            CONSTRAINT [ck_portfolio_risk_states_mode_flags]
                CHECK
                (
                    ([current_operating_mode] IN ('NORMAL','RESTRICTED')
                        AND [allows_new_exposure] = 1
                        AND [risk_multiplier] > 0)
                    OR
                    ([current_operating_mode] IN ('CLOSE_ONLY','PAUSED','HALTED')
                        AND [allows_new_exposure] = 0
                        AND [risk_multiplier] = 0
                        AND [maximum_concurrent_new_positions] = 0)
                ),
            CONSTRAINT [ck_portfolio_risk_states_exit_safety]
                CHECK ([allows_risk_reducing_exits] = 1),
            CONSTRAINT [ck_portfolio_risk_states_json]
                CHECK (ISJSON([reason_codes_json]) = 1),
            CONSTRAINT [ck_portfolio_risk_states_time]
                CHECK ([evaluated_at_utc] >= [as_of_utc])
        );

        CREATE INDEX [ix_portfolio_risk_states_latest]
            ON [risk].[portfolio_risk_states] ([environment], [as_of_utc] DESC)
            INCLUDE
            (
                [portfolio_code], [current_operating_mode], [allows_new_exposure],
                [risk_multiplier], [daily_loss_fraction], [portfolio_drawdown_fraction]
            );
    END;

    IF OBJECT_ID(N'[risk].[portfolio_risk_state_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[portfolio_risk_state_events]
        (
            [portfolio_risk_state_event_id] bigint IDENTITY(1,1) NOT NULL,
            [portfolio_risk_state_event_uid] uniqueidentifier NOT NULL,
            [portfolio_risk_state_id] bigint NOT NULL,
            [source_pnl_snapshot_id] bigint NOT NULL,
            [portfolio_snapshot_id] bigint NOT NULL,
            [risk_policy_id] bigint NOT NULL,
            [event_sequence] int NOT NULL,
            [resulting_state_version] int NOT NULL,
            [operating_mode_before] varchar(20) NULL,
            [operating_mode_after] varchar(20) NOT NULL,
            [allows_new_exposure_after] bit NOT NULL,
            [allows_risk_reducing_exits_after] bit NOT NULL,
            [risk_multiplier_after] decimal(9,8) NOT NULL,
            [maximum_concurrent_new_positions_after] int NOT NULL,
            [equity_amount] decimal(19,6) NOT NULL,
            [daily_pnl_amount] decimal(19,6) NOT NULL,
            [weekly_pnl_amount] decimal(19,6) NOT NULL,
            [daily_loss_fraction] decimal(9,8) NOT NULL,
            [weekly_loss_fraction] decimal(9,8) NOT NULL,
            [strategy_drawdown_fraction] decimal(9,8) NOT NULL,
            [portfolio_drawdown_fraction] decimal(9,8) NOT NULL,
            [reason_codes_json] nvarchar(max) NOT NULL,
            [occurred_at_utc] datetime2(7) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(100) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_risk_state_events_created_at] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_portfolio_risk_state_events]
                PRIMARY KEY CLUSTERED ([portfolio_risk_state_event_id]),
            CONSTRAINT [uq_portfolio_risk_state_events_uid]
                UNIQUE ([portfolio_risk_state_event_uid]),
            CONSTRAINT [uq_portfolio_risk_state_events_source]
                UNIQUE ([source_pnl_snapshot_id]),
            CONSTRAINT [uq_portfolio_risk_state_events_sequence]
                UNIQUE ([portfolio_risk_state_id], [event_sequence]),
            CONSTRAINT [uq_portfolio_risk_state_events_version]
                UNIQUE ([portfolio_risk_state_id], [resulting_state_version]),
            CONSTRAINT [fk_portfolio_risk_state_events_state]
                FOREIGN KEY ([portfolio_risk_state_id])
                REFERENCES [risk].[portfolio_risk_states] ([portfolio_risk_state_id]),
            CONSTRAINT [fk_portfolio_risk_state_events_source]
                FOREIGN KEY ([source_pnl_snapshot_id])
                REFERENCES [portfolio].[pnl_snapshots] ([pnl_snapshot_id]),
            CONSTRAINT [fk_portfolio_risk_state_events_snapshot]
                FOREIGN KEY ([portfolio_snapshot_id])
                REFERENCES [risk].[portfolio_snapshots] ([portfolio_snapshot_id]),
            CONSTRAINT [fk_portfolio_risk_state_events_policy]
                FOREIGN KEY ([risk_policy_id])
                REFERENCES [risk].[risk_policies] ([risk_policy_id]),
            CONSTRAINT [ck_portfolio_risk_state_events_sequence]
                CHECK ([event_sequence] >= 1 AND [resulting_state_version] >= 1),
            CONSTRAINT [ck_portfolio_risk_state_events_modes]
                CHECK
                (
                    ([operating_mode_before] IS NULL OR [operating_mode_before] IN ('NORMAL','RESTRICTED','CLOSE_ONLY','PAUSED','HALTED'))
                    AND [operating_mode_after] IN ('NORMAL','RESTRICTED','CLOSE_ONLY','PAUSED','HALTED')
                ),
            CONSTRAINT [ck_portfolio_risk_state_events_values]
                CHECK
                (
                    [risk_multiplier_after] BETWEEN 0 AND 1
                    AND [maximum_concurrent_new_positions_after] >= 0
                    AND [equity_amount] >= 0
                    AND [daily_loss_fraction] BETWEEN 0 AND 1
                    AND [weekly_loss_fraction] BETWEEN 0 AND 1
                    AND [strategy_drawdown_fraction] BETWEEN 0 AND 1
                    AND [portfolio_drawdown_fraction] BETWEEN 0 AND 1
                ),
            CONSTRAINT [ck_portfolio_risk_state_events_exit_safety]
                CHECK ([allows_risk_reducing_exits_after] = 1),
            CONSTRAINT [ck_portfolio_risk_state_events_json]
                CHECK (ISJSON([reason_codes_json]) = 1)
        );

        CREATE INDEX [ix_portfolio_risk_state_events_latest]
            ON [risk].[portfolio_risk_state_events]
                ([portfolio_risk_state_id], [event_sequence] DESC)
            INCLUDE ([operating_mode_after], [occurred_at_utc], [source_pnl_snapshot_id]);
    END;

    IF OBJECT_ID(N'[risk].[portfolio_risk_projection_work_items]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[portfolio_risk_projection_work_items]
        (
            [portfolio_risk_projection_work_item_id] bigint IDENTITY(1,1) NOT NULL,
            [request_uid] uniqueidentifier NOT NULL,
            [source_pnl_snapshot_id] bigint NOT NULL,
            [source_pnl_snapshot_uid] uniqueidentifier NOT NULL,
            [portfolio_id] bigint NOT NULL,
            [portfolio_code] varchar(100) NOT NULL,
            [risk_policy_id] bigint NOT NULL,
            [risk_policy_version] varchar(100) NOT NULL,
            [as_of_utc] datetime2(7) NOT NULL,
            [projection_policy_version] varchar(100) NOT NULL,
            [payload_json] nvarchar(max) NOT NULL,
            [current_status] varchar(30) NOT NULL,
            [attempt_count] int NOT NULL
                CONSTRAINT [df_portfolio_risk_projection_attempt_count] DEFAULT (0),
            [next_attempt_at_utc] datetime2(7) NOT NULL,
            [lease_owner] nvarchar(200) NULL,
            [lease_expires_at_utc] datetime2(7) NULL,
            [result_status] varchar(30) NULL,
            [portfolio_snapshot_id] bigint NULL,
            [portfolio_risk_state_id] bigint NULL,
            [reason_codes_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_portfolio_risk_projection_reasons] DEFAULT (N'[]'),
            [last_error] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_risk_projection_created_at] DEFAULT SYSUTCDATETIME(),
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_risk_projection_updated_at] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_portfolio_risk_projection_work_items]
                PRIMARY KEY CLUSTERED ([portfolio_risk_projection_work_item_id]),
            CONSTRAINT [uq_portfolio_risk_projection_request]
                UNIQUE ([request_uid]),
            CONSTRAINT [uq_portfolio_risk_projection_source]
                UNIQUE ([source_pnl_snapshot_id]),
            CONSTRAINT [uq_portfolio_risk_projection_source_uid]
                UNIQUE ([source_pnl_snapshot_uid]),
            CONSTRAINT [fk_portfolio_risk_projection_source]
                FOREIGN KEY ([source_pnl_snapshot_id])
                REFERENCES [portfolio].[pnl_snapshots] ([pnl_snapshot_id]),
            CONSTRAINT [fk_portfolio_risk_projection_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [fk_portfolio_risk_projection_policy]
                FOREIGN KEY ([risk_policy_id])
                REFERENCES [risk].[risk_policies] ([risk_policy_id]),
            CONSTRAINT [fk_portfolio_risk_projection_snapshot]
                FOREIGN KEY ([portfolio_snapshot_id])
                REFERENCES [risk].[portfolio_snapshots] ([portfolio_snapshot_id]),
            CONSTRAINT [fk_portfolio_risk_projection_state]
                FOREIGN KEY ([portfolio_risk_state_id])
                REFERENCES [risk].[portfolio_risk_states] ([portfolio_risk_state_id]),
            CONSTRAINT [ck_portfolio_risk_projection_payload]
                CHECK (ISJSON([payload_json]) = 1),
            CONSTRAINT [ck_portfolio_risk_projection_reasons]
                CHECK (ISJSON([reason_codes_json]) = 1),
            CONSTRAINT [ck_portfolio_risk_projection_attempts]
                CHECK ([attempt_count] >= 0),
            CONSTRAINT [ck_portfolio_risk_projection_status]
                CHECK
                (
                    [current_status] IN
                    ('PENDING','LEASED','PROJECTED','DUPLICATE','RETRY_PENDING','REJECTED','FAILED','CANCELLED')
                ),
            CONSTRAINT [ck_portfolio_risk_projection_lease]
                CHECK
                (
                    ([current_status] = 'LEASED'
                        AND [lease_owner] IS NOT NULL
                        AND [lease_expires_at_utc] IS NOT NULL)
                    OR
                    ([current_status] <> 'LEASED'
                        AND [lease_owner] IS NULL
                        AND [lease_expires_at_utc] IS NULL)
                ),
            CONSTRAINT [ck_portfolio_risk_projection_terminal]
                CHECK
                (
                    ([current_status] IN ('PROJECTED','DUPLICATE')
                        AND [result_status] = [current_status]
                        AND [portfolio_snapshot_id] IS NOT NULL
                        AND [portfolio_risk_state_id] IS NOT NULL)
                    OR
                    ([current_status] NOT IN ('PROJECTED','DUPLICATE')
                        AND [result_status] IS NULL
                        AND [portfolio_snapshot_id] IS NULL
                        AND [portfolio_risk_state_id] IS NULL)
                )
        );

        CREATE INDEX [ix_portfolio_risk_projection_available]
            ON [risk].[portfolio_risk_projection_work_items]
                ([current_status], [next_attempt_at_utc], [portfolio_risk_projection_work_item_id])
            INCLUDE ([attempt_count], [portfolio_id], [source_pnl_snapshot_id]);

        CREATE INDEX [ix_portfolio_risk_projection_lease]
            ON [risk].[portfolio_risk_projection_work_items] ([lease_expires_at_utc])
            WHERE [current_status] = 'LEASED';
    END;

    UPDATE [operations].[database_metadata]
    SET [schema_baseline_version] = 'V0028',
        [updated_at_utc] = SYSUTCDATETIME(),
        [updated_by] = COALESCE(SUSER_SNAME(), N'UNKNOWN')
    WHERE [database_metadata_id] = 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
