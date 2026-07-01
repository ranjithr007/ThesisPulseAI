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

    IF OBJECT_ID(N'[intelligence].[signals]', N'U') IS NULL
        THROW 61901, 'V0004 intelligence.signals is required.', 1;

    IF OBJECT_ID(N'[intelligence].[signal_fusion_lineage]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[signal_fusion_lineage]
        (
            [signal_fusion_lineage_id] bigint IDENTITY(1,1) NOT NULL,
            [signal_id] bigint NOT NULL,
            [thesis_uid] uniqueidentifier NOT NULL,
            [thesis_request_uid] uniqueidentifier NOT NULL,
            [candidate_signal_uid] uniqueidentifier NOT NULL,
            [fusion_evidence_uid] uniqueidentifier NOT NULL,
            [source_candle_message_uid] uniqueidentifier NOT NULL,
            [confirmation_output_uid] uniqueidentifier NOT NULL,
            [confirmation_message_uid] uniqueidentifier NOT NULL,
            [fusion_engine_version] varchar(50) NOT NULL,
            [fusion_policy_version] varchar(100) NOT NULL,
            [weight_configuration_version] varchar(100) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_signal_fusion_lineage_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_signal_fusion_lineage]
                PRIMARY KEY CLUSTERED ([signal_fusion_lineage_id]),
            CONSTRAINT [uq_signal_fusion_lineage_signal]
                UNIQUE ([signal_id]),
            CONSTRAINT [uq_signal_fusion_lineage_candidate]
                UNIQUE ([candidate_signal_uid]),
            CONSTRAINT [uq_signal_fusion_lineage_evidence]
                UNIQUE ([fusion_evidence_uid]),
            CONSTRAINT [fk_signal_fusion_lineage_signal]
                FOREIGN KEY ([signal_id])
                REFERENCES [intelligence].[signals] ([signal_id]),
            CONSTRAINT [ck_signal_fusion_lineage_versions]
                CHECK
                (
                    LEN(LTRIM(RTRIM([fusion_engine_version]))) > 0
                    AND LEN(LTRIM(RTRIM([fusion_policy_version]))) > 0
                    AND LEN(LTRIM(RTRIM([weight_configuration_version]))) > 0
                )
        );

        CREATE INDEX [ix_signal_fusion_lineage_thesis]
            ON [intelligence].[signal_fusion_lineage]
            ([thesis_uid], [thesis_request_uid], [signal_id]);

        CREATE INDEX [ix_signal_fusion_lineage_source]
            ON [intelligence].[signal_fusion_lineage]
            ([source_candle_message_uid], [confirmation_output_uid], [signal_id]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
