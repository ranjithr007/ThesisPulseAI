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

IF SCHEMA_ID(N'market') IS NULL
    EXEC(N'CREATE SCHEMA [market] AUTHORIZATION [dbo];');

IF OBJECT_ID(N'[market].[live_feed_subscriptions]', N'U') IS NULL
BEGIN
    CREATE TABLE [market].[live_feed_subscriptions]
    (
        [live_feed_subscription_id] bigint IDENTITY(1,1) NOT NULL,
        [live_feed_subscription_uid] uniqueidentifier NOT NULL
            CONSTRAINT [df_live_feed_subscriptions_uid] DEFAULT NEWSEQUENTIALID(),
        [broker_instrument_mapping_id] bigint NOT NULL,
        [feed_mode] varchar(30) NOT NULL,
        [recovery_timeframe] varchar(20) NOT NULL,
        [priority] int NOT NULL CONSTRAINT [df_live_feed_subscriptions_priority] DEFAULT (100),
        [is_enabled] bit NOT NULL CONSTRAINT [df_live_feed_subscriptions_enabled] DEFAULT (1),
        [valid_from_utc] datetime2(7) NOT NULL
            CONSTRAINT [df_live_feed_subscriptions_valid_from] DEFAULT SYSUTCDATETIME(),
        [valid_to_utc] datetime2(7) NULL,
        [metadata_json] nvarchar(max) NULL,
        [created_at_utc] datetime2(7) NOT NULL
            CONSTRAINT [df_live_feed_subscriptions_created_at] DEFAULT SYSUTCDATETIME(),
        [created_by] nvarchar(256) NOT NULL,
        [updated_at_utc] datetime2(7) NOT NULL
            CONSTRAINT [df_live_feed_subscriptions_updated_at] DEFAULT SYSUTCDATETIME(),
        [updated_by] nvarchar(256) NOT NULL,
        [row_version] rowversion NOT NULL,
        CONSTRAINT [pk_live_feed_subscriptions]
            PRIMARY KEY CLUSTERED ([live_feed_subscription_id]),
        CONSTRAINT [uq_live_feed_subscriptions_uid]
            UNIQUE ([live_feed_subscription_uid]),
        CONSTRAINT [uq_live_feed_subscriptions_mapping_mode]
            UNIQUE ([broker_instrument_mapping_id], [feed_mode]),
        CONSTRAINT [fk_live_feed_subscriptions_mapping]
            FOREIGN KEY ([broker_instrument_mapping_id])
            REFERENCES [reference].[broker_instrument_mappings]
                ([broker_instrument_mapping_id]),
        CONSTRAINT [ck_live_feed_subscriptions_mode]
            CHECK ([feed_mode] IN ('ltpc', 'full', 'option_greeks', 'full_d30')),
        CONSTRAINT [ck_live_feed_subscriptions_timeframe]
            CHECK ([recovery_timeframe] IN ('1m', '5m', '15m', '1h', '1d')),
        CONSTRAINT [ck_live_feed_subscriptions_priority]
            CHECK ([priority] BETWEEN 1 AND 10000),
        CONSTRAINT [ck_live_feed_subscriptions_validity]
            CHECK ([valid_to_utc] IS NULL OR [valid_to_utc] > [valid_from_utc]),
        CONSTRAINT [ck_live_feed_subscriptions_metadata]
            CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
    );

    CREATE INDEX [ix_live_feed_subscriptions_active]
        ON [market].[live_feed_subscriptions]
        ([is_enabled], [feed_mode], [priority], [valid_from_utc])
        INCLUDE ([broker_instrument_mapping_id], [recovery_timeframe], [valid_to_utc]);
END;

IF OBJECT_ID(N'[market].[data_gap_events]', N'U') IS NULL
BEGIN
    CREATE TABLE [market].[data_gap_events]
    (
        [data_gap_event_id] bigint IDENTITY(1,1) NOT NULL,
        [data_gap_event_uid] uniqueidentifier NOT NULL
            CONSTRAINT [df_data_gap_events_uid] DEFAULT NEWSEQUENTIALID(),
        [data_source_id] bigint NOT NULL,
        [instrument_id] bigint NOT NULL,
        [timeframe] varchar(20) NOT NULL,
        [gap_start_utc] datetime2(7) NOT NULL,
        [gap_end_utc] datetime2(7) NOT NULL,
        [expected_record_count] int NOT NULL,
        [detected_at_utc] datetime2(7) NOT NULL,
        [status] varchar(20) NOT NULL,
        [recovery_attempt_count] int NOT NULL
            CONSTRAINT [df_data_gap_events_attempts] DEFAULT (0),
        [last_recovery_at_utc] datetime2(7) NULL,
        [recovered_at_utc] datetime2(7) NULL,
        [last_error_message] nvarchar(4000) NULL,
        [correlation_id] uniqueidentifier NOT NULL,
        [created_at_utc] datetime2(7) NOT NULL
            CONSTRAINT [df_data_gap_events_created_at] DEFAULT SYSUTCDATETIME(),
        [created_by] nvarchar(256) NOT NULL,
        [updated_at_utc] datetime2(7) NOT NULL
            CONSTRAINT [df_data_gap_events_updated_at] DEFAULT SYSUTCDATETIME(),
        [updated_by] nvarchar(256) NOT NULL,
        [row_version] rowversion NOT NULL,
        CONSTRAINT [pk_data_gap_events]
            PRIMARY KEY CLUSTERED ([data_gap_event_id]),
        CONSTRAINT [uq_data_gap_events_uid] UNIQUE ([data_gap_event_uid]),
        CONSTRAINT [uq_data_gap_events_scope]
            UNIQUE ([data_source_id], [instrument_id], [timeframe], [gap_start_utc], [gap_end_utc]),
        CONSTRAINT [fk_data_gap_events_source]
            FOREIGN KEY ([data_source_id]) REFERENCES [market].[data_sources] ([data_source_id]),
        CONSTRAINT [fk_data_gap_events_instrument]
            FOREIGN KEY ([instrument_id]) REFERENCES [reference].[instruments] ([instrument_id]),
        CONSTRAINT [ck_data_gap_events_timeframe]
            CHECK ([timeframe] IN ('1m', '5m', '15m', '1h', '1d')),
        CONSTRAINT [ck_data_gap_events_window]
            CHECK ([gap_end_utc] > [gap_start_utc]),
        CONSTRAINT [ck_data_gap_events_count]
            CHECK ([expected_record_count] > 0),
        CONSTRAINT [ck_data_gap_events_status]
            CHECK ([status] IN ('DETECTED', 'RECOVERING', 'RECOVERED', 'FAILED', 'IGNORED')),
        CONSTRAINT [ck_data_gap_events_attempts]
            CHECK ([recovery_attempt_count] >= 0)
    );

    CREATE INDEX [ix_data_gap_events_pending]
        ON [market].[data_gap_events]
        ([status], [detected_at_utc], [recovery_attempt_count])
        INCLUDE ([instrument_id], [timeframe], [gap_start_utc], [gap_end_utc]);
END;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
GO
