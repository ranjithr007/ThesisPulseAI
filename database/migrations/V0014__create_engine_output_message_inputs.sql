/*
Migration: V0014__create_engine_output_message_inputs.sql
Purpose:
  Link immutable intelligence outputs to exact normalized inbox messages, including
  quote publications consumed by the deterministic Order Flow Engine.
Dependencies:
  V0004__create_intelligence_and_signal_tables.sql
  V0009__create_operational_foundation_tables.sql
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
        THROW 61401, 'V0004 intelligence.engine_outputs is required.', 1;

    IF OBJECT_ID(N'[operations].[inbox_messages]', N'U') IS NULL
        THROW 61402, 'V0009 operations.inbox_messages is required.', 1;

    IF OBJECT_ID(N'[intelligence].[engine_output_message_inputs]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[engine_output_message_inputs]
        (
            [engine_output_message_input_id] bigint IDENTITY(1,1) NOT NULL,
            [engine_output_id] bigint NOT NULL,
            [inbox_message_id] bigint NOT NULL,
            [input_role] varchar(30) NOT NULL,
            [input_sequence] int NOT NULL,
            [consumed_at_utc] datetime2(7) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_engine_output_message_inputs_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_engine_output_message_inputs]
                PRIMARY KEY CLUSTERED ([engine_output_message_input_id]),
            CONSTRAINT [uq_engine_output_message_inputs_message]
                UNIQUE ([engine_output_id], [inbox_message_id]),
            CONSTRAINT [uq_engine_output_message_inputs_sequence]
                UNIQUE ([engine_output_id], [input_sequence]),
            CONSTRAINT [fk_engine_output_message_inputs_output]
                FOREIGN KEY ([engine_output_id])
                REFERENCES [intelligence].[engine_outputs] ([engine_output_id]),
            CONSTRAINT [fk_engine_output_message_inputs_inbox]
                FOREIGN KEY ([inbox_message_id])
                REFERENCES [operations].[inbox_messages] ([inbox_message_id]),
            CONSTRAINT [ck_engine_output_message_inputs_role]
                CHECK ([input_role] IN ('PRIMARY', 'QUOTE_CONTEXT', 'CONFIRMATION', 'CONTEXT')),
            CONSTRAINT [ck_engine_output_message_inputs_sequence]
                CHECK ([input_sequence] >= 1)
        );

        CREATE INDEX [ix_engine_output_message_inputs_output]
            ON [intelligence].[engine_output_message_inputs]
            ([engine_output_id], [input_sequence])
            INCLUDE ([inbox_message_id], [input_role], [consumed_at_utc]);

        CREATE INDEX [ix_engine_output_message_inputs_inbox]
            ON [intelligence].[engine_output_message_inputs]
            ([inbox_message_id], [engine_output_id]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
