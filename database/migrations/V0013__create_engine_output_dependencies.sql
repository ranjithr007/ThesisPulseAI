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
    THROW 61301, 'V0004 intelligence.engine_outputs is required.', 1;

IF OBJECT_ID(N'[intelligence].[engine_output_dependencies]', N'U') IS NULL
BEGIN
    CREATE TABLE [intelligence].[engine_output_dependencies]
    (
        [engine_output_dependency_id] bigint IDENTITY(1,1) NOT NULL,
        [downstream_engine_output_id] bigint NOT NULL,
        [upstream_engine_output_id] bigint NOT NULL,
        [instrument_id] bigint NOT NULL,
        [dependency_role] varchar(30) NOT NULL,
        [consumed_at_utc] datetime2(7) NOT NULL,
        [metadata_json] nvarchar(max) NULL,
        [created_at_utc] datetime2(7) NOT NULL
            CONSTRAINT [df_engine_output_dependencies_created_at_utc]
            DEFAULT SYSUTCDATETIME(),
        [created_by] nvarchar(256) NOT NULL,
        CONSTRAINT [pk_engine_output_dependencies]
            PRIMARY KEY CLUSTERED ([engine_output_dependency_id]),
        CONSTRAINT [uq_engine_output_dependencies]
            UNIQUE ([downstream_engine_output_id], [upstream_engine_output_id], [dependency_role]),
        CONSTRAINT [fk_engine_output_dependencies_downstream]
            FOREIGN KEY ([downstream_engine_output_id], [instrument_id])
            REFERENCES [intelligence].[engine_outputs]
                ([engine_output_id], [instrument_id]),
        CONSTRAINT [fk_engine_output_dependencies_upstream]
            FOREIGN KEY ([upstream_engine_output_id], [instrument_id])
            REFERENCES [intelligence].[engine_outputs]
                ([engine_output_id], [instrument_id]),
        CONSTRAINT [ck_engine_output_dependencies_role]
            CHECK
            (
                [dependency_role] IN
                ('FEATURE_SET', 'CONTEXT', 'CONFIRMATION', 'CONTRADICTION', 'HARD_GATE', 'QUALITY')
            ),
        CONSTRAINT [ck_engine_output_dependencies_not_self]
            CHECK ([downstream_engine_output_id] <> [upstream_engine_output_id]),
        CONSTRAINT [ck_engine_output_dependencies_metadata]
            CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
    );

    CREATE INDEX [ix_engine_output_dependencies_upstream]
        ON [intelligence].[engine_output_dependencies]
        ([upstream_engine_output_id], [downstream_engine_output_id])
        INCLUDE ([instrument_id], [dependency_role], [consumed_at_utc]);

    CREATE INDEX [ix_engine_output_dependencies_downstream]
        ON [intelligence].[engine_output_dependencies]
        ([downstream_engine_output_id], [dependency_role])
        INCLUDE ([upstream_engine_output_id], [instrument_id], [consumed_at_utc]);
END;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
GO
