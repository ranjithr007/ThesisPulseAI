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

IF OBJECT_ID(N'[operations].[outbox_messages]', N'U') IS NULL
    THROW 61101, 'V0009 operations.outbox_messages is required.', 1;

IF OBJECT_ID(N'[market].[candles]', N'U') IS NULL
    THROW 61102, 'V0003 market.candles is required.', 1;

IF OBJECT_ID(N'[operations].[consumer_checkpoints]', N'U') IS NULL
BEGIN
    CREATE TABLE [operations].[consumer_checkpoints]
    (
        [consumer_checkpoint_id] bigint IDENTITY(1,1) NOT NULL,
        [consumer_checkpoint_uid] uniqueidentifier NOT NULL
            CONSTRAINT [df_consumer_checkpoints_uid] DEFAULT NEWSEQUENTIALID(),
        [consumer_name] varchar(200) NOT NULL,
        [stream_name] varchar(200) NOT NULL,
        [partition_key] varchar(200) NOT NULL
            CONSTRAINT [df_consumer_checkpoints_partition] DEFAULT ('*'),
        [last_outbox_message_id] bigint NOT NULL,
        [last_message_uid] uniqueidentifier NOT NULL,
        [last_occurred_at_utc] datetime2(7) NOT NULL,
        [metadata_json] nvarchar(max) NULL,
        [created_at_utc] datetime2(7) NOT NULL
            CONSTRAINT [df_consumer_checkpoints_created_at] DEFAULT SYSUTCDATETIME(),
        [created_by] nvarchar(256) NOT NULL,
        [updated_at_utc] datetime2(7) NOT NULL
            CONSTRAINT [df_consumer_checkpoints_updated_at] DEFAULT SYSUTCDATETIME(),
        [updated_by] nvarchar(256) NOT NULL,
        [row_version] rowversion NOT NULL,
        CONSTRAINT [pk_consumer_checkpoints]
            PRIMARY KEY CLUSTERED ([consumer_checkpoint_id]),
        CONSTRAINT [uq_consumer_checkpoints_uid]
            UNIQUE ([consumer_checkpoint_uid]),
        CONSTRAINT [uq_consumer_checkpoints_scope]
            UNIQUE ([consumer_name], [stream_name], [partition_key]),
        CONSTRAINT [fk_consumer_checkpoints_outbox]
            FOREIGN KEY ([last_outbox_message_id])
            REFERENCES [operations].[outbox_messages] ([outbox_message_id]),
        CONSTRAINT [ck_consumer_checkpoints_position]
            CHECK ([last_outbox_message_id] > 0),
        CONSTRAINT [ck_consumer_checkpoints_metadata]
            CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
    );

    CREATE INDEX [ix_consumer_checkpoints_stream]
        ON [operations].[consumer_checkpoints]
        ([stream_name], [consumer_name], [last_outbox_message_id]);
END;

EXEC(N'
CREATE OR ALTER TRIGGER [market].[tr_candles_publish_v1]
ON [market].[candles]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH publication AS
    (
        SELECT
            CONVERT(uniqueidentifier, SUBSTRING(HASHBYTES(
                ''SHA2_256'',
                CONCAT(
                    ''market.candle.published.v1|'',
                    mapping.[broker_instrument_key], ''|'',
                    inserted.[timeframe], ''|'',
                    CONVERT(varchar(33), inserted.[open_at_utc], 126), ''|'',
                    inserted.[revision], ''|'', inserted.[is_closed]
                )), 1, 16)) AS [message_uid],
            broker.[broker_code] AS [provider_code],
            mapping.[broker_instrument_key] AS [instrument_key],
            inserted.*,
            observation.[correlation_id],
            observation.[source_event_id]
        FROM inserted
        INNER JOIN [reference].[broker_instrument_mappings] mapping
            ON mapping.[instrument_id] = inserted.[instrument_id]
           AND mapping.[is_active] = 1
           AND mapping.[valid_to_date] IS NULL
        INNER JOIN [reference].[brokers] broker
            ON broker.[broker_id] = mapping.[broker_id]
        INNER JOIN [market].[source_observations] observation
            ON observation.[source_observation_id] = inserted.[source_observation_id]
    )
    INSERT INTO [operations].[outbox_messages]
    (
        [message_uid], [contract_version], [environment], [message_type],
        [destination], [partition_key], [aggregate_type], [aggregate_uid],
        [idempotency_key], [correlation_id], [causation_id],
        [source_service], [source_version], [generated_at_utc],
        [not_before_utc], [payload_json], [payload_hash], [headers_json],
        [status], [attempt_count], [max_attempts], [created_by], [updated_by]
    )
    SELECT
        publication.[message_uid], ''1.0'', ''PAPER'',
        ''market.candle.published.v1'', ''MARKET_DATA_FANOUT'',
        publication.[instrument_key], ''MARKET_CANDLE'',
        publication.[candle_uid], CONVERT(varchar(36), publication.[message_uid]),
        publication.[correlation_id], NULL,
        ''ThesisPulse.MarketData.Service'', publication.[source_version],
        publication.[close_at_utc], publication.[close_at_utc],
        payload.[payload_json],
        CONVERT(varchar(64), HASHBYTES(''SHA2_256'', payload.[payload_json]), 2),
        N''{"configurationVersion":"market-data-publication-v1.0.0"}'',
        ''PENDING'', 0, 5,
        N''ThesisPulse.MarketData.Service'', N''ThesisPulse.MarketData.Service''
    FROM publication
    CROSS APPLY
    (
        SELECT
            publication.[provider_code] AS [providerCode],
            publication.[instrument_key] AS [instrumentKey],
            publication.[timeframe] AS [timeframe],
            publication.[open_at_utc] AS [openAtUtc],
            publication.[close_at_utc] AS [closeAtUtc],
            publication.[open_price] AS [openPrice],
            publication.[high_price] AS [highPrice],
            publication.[low_price] AS [lowPrice],
            publication.[close_price] AS [closePrice],
            publication.[volume_qty] AS [volumeQuantity],
            CAST(NULL AS decimal(19,6)) AS [openInterest],
            publication.[is_closed] AS [isClosed],
            publication.[is_provisional] AS [isProvisional],
            publication.[revision] AS [revision],
            publication.[quality_status] AS [qualityStatus],
            publication.[is_usable_for_new_exposure] AS [isUsableForNewExposure],
            publication.[received_at_utc] AS [receivedAtUtc],
            publication.[source_version] AS [sourceVersion]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    ) payload([payload_json])
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM [operations].[outbox_messages] existing
        WHERE existing.[message_uid] = publication.[message_uid]
    );
END;
');

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
GO
