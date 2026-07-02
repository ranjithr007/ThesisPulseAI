/*
Migration: V0022__bridge_signal_risk_to_trade_plans.sql
Purpose:
  Extend the existing Trade Plan authority to accept direct lineage from the authoritative
  Phase 3.2 signal-risk evaluation, and add a durable leased construction queue.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'[risk].[trade_plans]', N'U') IS NULL
        THROW 59201, 'V0006 is required: risk.trade_plans does not exist.', 1;
    IF OBJECT_ID(N'[risk].[signal_risk_evaluations]', N'U') IS NULL
        THROW 59202, 'V0020 is required: risk.signal_risk_evaluations does not exist.', 1;

    IF COL_LENGTH(N'risk.trade_plans', N'signal_risk_evaluation_id') IS NULL
    BEGIN
        ALTER TABLE [risk].[trade_plans]
            ADD [signal_risk_evaluation_id] bigint NULL;

        ALTER TABLE [risk].[trade_plans]
            DROP CONSTRAINT [fk_trade_plans_risk_decision];
        ALTER TABLE [risk].[trade_plans]
            DROP CONSTRAINT [uq_trade_plans_decision_version];
        DROP INDEX [ux_trade_plans_current_decision] ON [risk].[trade_plans];

        ALTER TABLE [risk].[trade_plans]
            ALTER COLUMN [risk_decision_id] bigint NULL;

        ALTER TABLE [risk].[trade_plans]
            ADD CONSTRAINT [fk_trade_plans_risk_decision]
                FOREIGN KEY ([risk_decision_id], [instrument_id])
                REFERENCES [risk].[risk_decisions] ([risk_decision_id], [instrument_id]);
        ALTER TABLE [risk].[trade_plans]
            ADD CONSTRAINT [fk_trade_plans_signal_risk_evaluation]
                FOREIGN KEY ([signal_risk_evaluation_id])
                REFERENCES [risk].[signal_risk_evaluations] ([signal_risk_evaluation_id]);
        ALTER TABLE [risk].[trade_plans]
            ADD CONSTRAINT [ck_trade_plans_risk_lineage]
                CHECK
                (
                    ([risk_decision_id] IS NOT NULL AND [signal_risk_evaluation_id] IS NULL)
                    OR
                    ([risk_decision_id] IS NULL AND [signal_risk_evaluation_id] IS NOT NULL)
                );

        CREATE UNIQUE INDEX [ux_trade_plans_legacy_decision_version]
            ON [risk].[trade_plans] ([risk_decision_id], [plan_version])
            WHERE [risk_decision_id] IS NOT NULL;
        CREATE UNIQUE INDEX [ux_trade_plans_current_legacy_decision]
            ON [risk].[trade_plans] ([risk_decision_id])
            WHERE [is_current] = 1 AND [risk_decision_id] IS NOT NULL;
        CREATE UNIQUE INDEX [ux_trade_plans_signal_risk_version]
            ON [risk].[trade_plans] ([signal_risk_evaluation_id], [plan_version])
            WHERE [signal_risk_evaluation_id] IS NOT NULL;
        CREATE UNIQUE INDEX [ux_trade_plans_current_signal_risk]
            ON [risk].[trade_plans] ([signal_risk_evaluation_id])
            WHERE [is_current] = 1 AND [signal_risk_evaluation_id] IS NOT NULL;
    END;

    IF OBJECT_ID(N'[risk].[trade_plan_work_items]', N'U') IS NULL
    BEGIN
        CREATE TABLE [risk].[trade_plan_work_items]
        (
            [trade_plan_work_item_id] bigint IDENTITY(1,1) NOT NULL,
            [signal_risk_evaluation_id] bigint NOT NULL,
            [source_message_uid] uniqueidentifier NOT NULL,
            [command_uid] uniqueidentifier NOT NULL,
            [request_uid] uniqueidentifier NOT NULL,
            [risk_decision_uid] uniqueidentifier NOT NULL,
            [signal_uid] uniqueidentifier NOT NULL,
            [thesis_uid] uniqueidentifier NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [payload_json] nvarchar(max) NOT NULL,
            [current_status] varchar(30) NOT NULL,
            [attempt_count] int NOT NULL CONSTRAINT [df_trade_plan_work_attempt_count] DEFAULT (0),
            [next_attempt_at_utc] datetime2(7) NOT NULL,
            [lease_owner] nvarchar(200) NULL,
            [lease_expires_at_utc] datetime2(7) NULL,
            [last_error] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL CONSTRAINT [df_trade_plan_work_created_at] DEFAULT SYSUTCDATETIME(),
            [updated_at_utc] datetime2(7) NOT NULL CONSTRAINT [df_trade_plan_work_updated_at] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_trade_plan_work_items] PRIMARY KEY CLUSTERED ([trade_plan_work_item_id]),
            CONSTRAINT [uq_trade_plan_work_evaluation] UNIQUE ([signal_risk_evaluation_id]),
            CONSTRAINT [uq_trade_plan_work_source_message] UNIQUE ([source_message_uid]),
            CONSTRAINT [uq_trade_plan_work_command] UNIQUE ([command_uid]),
            CONSTRAINT [uq_trade_plan_work_risk_decision] UNIQUE ([risk_decision_uid]),
            CONSTRAINT [fk_trade_plan_work_evaluation]
                FOREIGN KEY ([signal_risk_evaluation_id])
                REFERENCES [risk].[signal_risk_evaluations] ([signal_risk_evaluation_id]),
            CONSTRAINT [ck_trade_plan_work_payload_json] CHECK (ISJSON([payload_json]) = 1),
            CONSTRAINT [ck_trade_plan_work_attempt_count] CHECK ([attempt_count] >= 0),
            CONSTRAINT [ck_trade_plan_work_status] CHECK
            (
                [current_status] IN
                ('PENDING', 'LEASED', 'COMPLETED', 'RETRY_PENDING', 'REJECTED', 'EXPIRED', 'FAILED', 'CANCELLED')
            ),
            CONSTRAINT [ck_trade_plan_work_lease] CHECK
            (
                ([current_status] = 'LEASED' AND [lease_owner] IS NOT NULL AND [lease_expires_at_utc] IS NOT NULL)
                OR
                ([current_status] <> 'LEASED' AND [lease_owner] IS NULL AND [lease_expires_at_utc] IS NULL)
            )
        );

        CREATE INDEX [ix_trade_plan_work_available]
            ON [risk].[trade_plan_work_items] ([current_status], [next_attempt_at_utc], [trade_plan_work_item_id])
            INCLUDE ([attempt_count], [signal_risk_evaluation_id], [risk_decision_uid], [source_message_uid]);
        CREATE INDEX [ix_trade_plan_work_lease]
            ON [risk].[trade_plan_work_items] ([lease_expires_at_utc])
            WHERE [current_status] = 'LEASED';
    END;

    UPDATE [operations].[database_metadata]
    SET [schema_baseline_version] = 'V0022',
        [updated_at_utc] = SYSUTCDATETIME(),
        [updated_by] = COALESCE(SUSER_SNAME(), N'UNKNOWN')
    WHERE [database_metadata_id] = 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
