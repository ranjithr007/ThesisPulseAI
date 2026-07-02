/*
Migration: V0028__paper_portfolio_risk_state.sql
Purpose:
  Add durable, replay-safe projection of immutable PAPER portfolio P&L snapshots
  into authoritative portfolio-risk snapshots and current operating state.
Authority boundary:
  Risk Service owns operating mode and new-exposure permission. This migration
  does not introduce liquidation, exit-order, broker, LIVE or policy-mutation authority.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'[portfolio].[pnl_snapshots]', N'U') IS NULL
        THROW 59801, 'V0008 is required: portfolio.pnl_snapshots does not exist.', 1;
    IF OBJECT_ID(N'[risk].[risk_policies]', N'U') IS NULL
        THROW 59802, 'Risk policy baseline is required: risk.risk_policies does not exist.', 1;
    IF OBJECT_ID(N'[risk].[active_policy_assignments]', N'U') IS NULL
        THROW 59803, 'Risk policy baseline is required: risk.active_policy_assignments does not exist.', 1;

    IF OBJECT_ID(N'[risk].[portfolio_risk_snapshots]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[portfolio_risk_snapshots]
        (
            [portfolio_risk_snapshot_id] bigint IDENTITY(1,1) NOT NULL,
            [risk_snapshot_uid] uniqueidentifier NOT NULL,
            [source_pnl_snapshot_uid] uniqueidentifier NOT NULL,
            [policy_uid] uniqueidentifier NOT NULL,
            [policy_version] varchar(100) NOT NULL,
            [portfolio_code] varchar(100) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [currency_code] char(3) NOT NULL,
            [operating_mode] varchar(30) NOT NULL,
            [effective_risk_multiplier] decimal(19,8) NOT NULL,
            [daily_pnl_amount] decimal(19,4) NOT NULL,
            [weekly_pnl_amount] decimal(19,4) NOT NULL,
            [daily_loss_fraction] decimal(19,8) NOT NULL,
            [weekly_loss_fraction] decimal(19,8) NOT NULL,
            [strategy_drawdown_fraction] decimal(19,8) NOT NULL,
            [portfolio_drawdown_fraction] decimal(19,8) NOT NULL,
            [new_exposure_allowed] bit NOT NULL,
            [risk_reducing_exit_allowed] bit NOT NULL,
            [reasons_json] nvarchar(max) NOT NULL,
            [source_as_of_utc] datetime2(7) NOT NULL,
            [evaluated_at_utc] datetime2(7) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_risk_snapshot_created] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_portfolio_risk_snapshots]
                PRIMARY KEY CLUSTERED ([portfolio_risk_snapshot_id]),
            CONSTRAINT [uq_portfolio_risk_snapshot_uid]
                UNIQUE ([risk_snapshot_uid]),
            CONSTRAINT [uq_portfolio_risk_source_policy]
                UNIQUE ([source_pnl_snapshot_uid], [policy_uid], [policy_version]),
            CONSTRAINT [ck_portfolio_risk_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_portfolio_risk_mode]
                CHECK ([operating_mode] IN ('NORMAL', 'RESTRICTED', 'CLOSE_ONLY', 'PAUSED', 'HALTED')),
            CONSTRAINT [ck_portfolio_risk_multiplier]
                CHECK ([effective_risk_multiplier] >= 0 AND [effective_risk_multiplier] <= 1),
            CONSTRAINT [ck_portfolio_risk_fractions]
                CHECK ([daily_loss_fraction] >= 0 AND [weekly_loss_fraction] >= 0
                    AND [strategy_drawdown_fraction] >= 0 AND [portfolio_drawdown_fraction] >= 0),
            CONSTRAINT [ck_portfolio_risk_reasons]
                CHECK (ISJSON([reasons_json]) = 1),
            CONSTRAINT [ck_portfolio_risk_permissions]
                CHECK
                (
                    [risk_reducing_exit_allowed] = 1
                    AND
                    (
                        ([operating_mode] IN ('NORMAL', 'RESTRICTED') AND [new_exposure_allowed] = 1)
                        OR
                        ([operating_mode] IN ('CLOSE_ONLY', 'PAUSED', 'HALTED') AND [new_exposure_allowed] = 0)
                    )
                )
        );

        CREATE INDEX [ix_portfolio_risk_latest]
            ON [risk].[portfolio_risk_snapshots]
                ([portfolio_code], [environment], [source_as_of_utc] DESC, [portfolio_risk_snapshot_id] DESC);
    END;

    IF OBJECT_ID(N'[risk].[portfolio_control_states]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[portfolio_control_states]
        (
            [portfolio_control_state_id] bigint IDENTITY(1,1) NOT NULL,
            [portfolio_code] varchar(100) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [risk_snapshot_uid] uniqueidentifier NOT NULL,
            [operating_mode] varchar(30) NOT NULL,
            [effective_risk_multiplier] decimal(19,8) NOT NULL,
            [new_exposure_allowed] bit NOT NULL,
            [risk_reducing_exit_allowed] bit NOT NULL,
            [source_as_of_utc] datetime2(7) NOT NULL,
            [version_number] bigint NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_control_state_updated] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_portfolio_control_states]
                PRIMARY KEY CLUSTERED ([portfolio_control_state_id]),
            CONSTRAINT [uq_portfolio_control_state]
                UNIQUE ([portfolio_code], [environment]),
            CONSTRAINT [uq_portfolio_control_risk_snapshot]
                UNIQUE ([risk_snapshot_uid]),
            CONSTRAINT [ck_portfolio_control_version]
                CHECK ([version_number] > 0),
            CONSTRAINT [ck_portfolio_control_mode]
                CHECK ([operating_mode] IN ('NORMAL', 'RESTRICTED', 'CLOSE_ONLY', 'PAUSED', 'HALTED'))
        );
    END;

    IF OBJECT_ID(N'[risk].[portfolio_risk_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[portfolio_risk_events]
        (
            [portfolio_risk_event_id] bigint IDENTITY(1,1) NOT NULL,
            [event_uid] uniqueidentifier NOT NULL,
            [risk_snapshot_uid] uniqueidentifier NOT NULL,
            [portfolio_code] varchar(100) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [previous_operating_mode] varchar(30) NULL,
            [operating_mode] varchar(30) NOT NULL,
            [reasons_json] nvarchar(max) NOT NULL,
            [occurred_at_utc] datetime2(7) NOT NULL,
            CONSTRAINT [pk_portfolio_risk_events]
                PRIMARY KEY CLUSTERED ([portfolio_risk_event_id]),
            CONSTRAINT [uq_portfolio_risk_event_uid]
                UNIQUE ([event_uid]),
            CONSTRAINT [uq_portfolio_risk_event_snapshot]
                UNIQUE ([risk_snapshot_uid]),
            CONSTRAINT [ck_portfolio_risk_event_reasons]
                CHECK (ISJSON([reasons_json]) = 1)
        );
    END;

    IF OBJECT_ID(N'[risk].[portfolio_risk_work_items]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[portfolio_risk_work_items]
        (
            [portfolio_risk_work_item_id] bigint IDENTITY(1,1) NOT NULL,
            [request_uid] uniqueidentifier NOT NULL,
            [source_pnl_snapshot_uid] uniqueidentifier NOT NULL,
            [policy_uid] uniqueidentifier NOT NULL,
            [policy_version] varchar(100) NOT NULL,
            [portfolio_code] varchar(100) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [source_as_of_utc] datetime2(7) NOT NULL,
            [current_status] varchar(30) NOT NULL,
            [attempt_count] int NOT NULL
                CONSTRAINT [df_portfolio_risk_work_attempt] DEFAULT (0),
            [next_attempt_at_utc] datetime2(7) NOT NULL,
            [lease_owner] nvarchar(200) NULL,
            [lease_expires_at_utc] datetime2(7) NULL,
            [result_status] varchar(30) NULL,
            [risk_snapshot_uid] uniqueidentifier NULL,
            [reasons_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_portfolio_risk_work_reasons] DEFAULT (N'[]'),
            [last_error] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_risk_work_created] DEFAULT SYSUTCDATETIME(),
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_portfolio_risk_work_updated] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_portfolio_risk_work_items]
                PRIMARY KEY CLUSTERED ([portfolio_risk_work_item_id]),
            CONSTRAINT [uq_portfolio_risk_work_request]
                UNIQUE ([request_uid]),
            CONSTRAINT [uq_portfolio_risk_work_source]
                UNIQUE ([source_pnl_snapshot_uid], [policy_uid], [policy_version]),
            CONSTRAINT [ck_portfolio_risk_work_status]
                CHECK ([current_status] IN ('PENDING', 'LEASED', 'EVALUATED', 'DUPLICATE', 'RETRY_PENDING', 'REJECTED', 'FAILED', 'CANCELLED')),
            CONSTRAINT [ck_portfolio_risk_work_attempt]
                CHECK ([attempt_count] >= 0),
            CONSTRAINT [ck_portfolio_risk_work_reasons]
                CHECK (ISJSON([reasons_json]) = 1),
            CONSTRAINT [ck_portfolio_risk_work_lease]
                CHECK
                (
                    ([current_status] = 'LEASED' AND [lease_owner] IS NOT NULL AND [lease_expires_at_utc] IS NOT NULL)
                    OR
                    ([current_status] <> 'LEASED' AND [lease_owner] IS NULL AND [lease_expires_at_utc] IS NULL)
                ),
            CONSTRAINT [ck_portfolio_risk_work_result]
                CHECK
                (
                    ([current_status] IN ('EVALUATED', 'DUPLICATE') AND [result_status] = [current_status] AND [risk_snapshot_uid] IS NOT NULL)
                    OR
                    ([current_status] NOT IN ('EVALUATED', 'DUPLICATE') AND [result_status] IS NULL AND [risk_snapshot_uid] IS NULL)
                )
        );

        CREATE INDEX [ix_portfolio_risk_work_available]
            ON [risk].[portfolio_risk_work_items]
                ([current_status], [next_attempt_at_utc], [portfolio_risk_work_item_id])
            INCLUDE ([attempt_count], [portfolio_code], [environment], [source_as_of_utc]);

        CREATE INDEX [ix_portfolio_risk_work_lease]
            ON [risk].[portfolio_risk_work_items] ([lease_expires_at_utc])
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
