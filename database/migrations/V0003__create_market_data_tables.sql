/*
Migration: V0003__create_market_data_tables.sql
Purpose:
  Create market-data source, ingestion, immutable observation, normalized candle,
  ingestion-cursor and quality-assessment storage.
Dependencies:
  V0001__create_schemas_and_migration_metadata.sql
  V0002__create_reference_tables.sql
Expected runtime impact:
  Additive DDL only. No market data is scanned or backfilled.
Locking considerations:
  Schema modification locks are acquired while tables, constraints and indexes are created.
Backward-compatibility window:
  Fully additive.
Data migration requirements:
  None.
Verification script:
  database/verification/V0003__verify_market_data_tables.sql
Recovery plan:
  Roll forward with a later migration. Destructive rollback is limited to disposable local databases.
*/

-- Required by SQL Server for filtered indexes and future writes that affect them.
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
        THROW 53001, 'V0001 is required: schema market does not exist.', 1;

    IF OBJECT_ID(N'[reference].[instruments]', N'U') IS NULL
        THROW 53002, 'V0002 is required: reference.instruments does not exist.', 1;

    IF OBJECT_ID(N'[reference].[trading_sessions]', N'U') IS NULL
        THROW 53003, 'V0002 is required: reference.trading_sessions does not exist.', 1;

    IF OBJECT_ID(N'[market].[data_sources]', N'U') IS NULL
    BEGIN
        CREATE TABLE [market].[data_sources]
        (
            [data_source_id] bigint IDENTITY(1,1) NOT NULL,
            [data_source_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_data_sources_uid] DEFAULT NEWSEQUENTIALID(),
            [source_code] varchar(50) NOT NULL,
            [source_name] nvarchar(200) NOT NULL,
            [source_type] varchar(30) NOT NULL,
            [transport_type] varchar(30) NOT NULL,
            [is_authoritative] bit NOT NULL
                CONSTRAINT [df_data_sources_authoritative] DEFAULT (0),
            [is_active] bit NOT NULL
                CONSTRAINT [df_data_sources_active] DEFAULT (1),
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_data_sources_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_data_sources_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_data_sources]
                PRIMARY KEY CLUSTERED ([data_source_id]),
            CONSTRAINT [uq_data_sources_uid]
                UNIQUE ([data_source_uid]),
            CONSTRAINT [uq_data_sources_code]
                UNIQUE ([source_code]),
            CONSTRAINT [ck_data_sources_source_type]
                CHECK ([source_type] IN ('EXCHANGE', 'BROKER', 'VENDOR', 'DERIVED', 'REPLAY')),
            CONSTRAINT [ck_data_sources_transport_type]
                CHECK ([transport_type] IN ('REST', 'WEBSOCKET', 'FILE', 'DATABASE', 'INTERNAL'))
        );

        CREATE INDEX [ix_data_sources_active]
            ON [market].[data_sources] ([is_active], [source_type], [source_code]);
    END;

    IF OBJECT_ID(N'[market].[ingestion_batches]', N'U') IS NULL
    BEGIN
        CREATE TABLE [market].[ingestion_batches]
        (
            [ingestion_batch_id] bigint IDENTITY(1,1) NOT NULL,
            [ingestion_batch_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_ingestion_batches_uid] DEFAULT NEWSEQUENTIALID(),
            [data_source_id] bigint NOT NULL,
            [instrument_id] bigint NULL,
            [data_type] varchar(50) NOT NULL,
            [timeframe] varchar(20) NULL,
            [ingestion_mode] varchar(20) NOT NULL,
            [source_request_id] varchar(200) NULL,
            [requested_from_utc] datetime2(7) NULL,
            [requested_to_utc] datetime2(7) NULL,
            [status] varchar(20) NOT NULL,
            [expected_record_count] bigint NULL,
            [received_record_count] bigint NOT NULL
                CONSTRAINT [df_ingestion_batches_received_count] DEFAULT (0),
            [accepted_record_count] bigint NOT NULL
                CONSTRAINT [df_ingestion_batches_accepted_count] DEFAULT (0),
            [rejected_record_count] bigint NOT NULL
                CONSTRAINT [df_ingestion_batches_rejected_count] DEFAULT (0),
            [duplicate_record_count] bigint NOT NULL
                CONSTRAINT [df_ingestion_batches_duplicate_count] DEFAULT (0),
            [started_at_utc] datetime2(7) NOT NULL,
            [completed_at_utc] datetime2(7) NULL,
            [correlation_id] uniqueidentifier NULL,
            [error_code] varchar(100) NULL,
            [error_message] nvarchar(4000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_ingestion_batches_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_ingestion_batches_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_ingestion_batches]
                PRIMARY KEY CLUSTERED ([ingestion_batch_id]),
            CONSTRAINT [uq_ingestion_batches_uid]
                UNIQUE ([ingestion_batch_uid]),
            CONSTRAINT [uq_ingestion_batches_id_source]
                UNIQUE ([ingestion_batch_id], [data_source_id]),
            CONSTRAINT [fk_ingestion_batches_source]
                FOREIGN KEY ([data_source_id])
                REFERENCES [market].[data_sources] ([data_source_id]),
            CONSTRAINT [fk_ingestion_batches_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [ck_ingestion_batches_data_type]
                CHECK ([data_type] IN ('CANDLE', 'QUOTE', 'TRADE', 'OPEN_INTEREST', 'REFERENCE', 'BROKER_STATE')),
            CONSTRAINT [ck_ingestion_batches_timeframe]
                CHECK
                (
                    ([timeframe] IS NULL OR [timeframe] IN ('1m', '5m', '15m', '1h', '1d'))
                    AND ([data_type] <> 'CANDLE' OR [timeframe] IS NOT NULL)
                ),
            CONSTRAINT [ck_ingestion_batches_mode]
                CHECK ([ingestion_mode] IN ('LIVE', 'HISTORICAL', 'BACKFILL', 'REPLAY')),
            CONSTRAINT [ck_ingestion_batches_status]
                CHECK ([status] IN ('STARTED', 'SUCCEEDED', 'PARTIAL', 'FAILED', 'CANCELLED')),
            CONSTRAINT [ck_ingestion_batches_window]
                CHECK
                (
                    [requested_from_utc] IS NULL
                    OR [requested_to_utc] IS NULL
                    OR [requested_to_utc] >= [requested_from_utc]
                ),
            CONSTRAINT [ck_ingestion_batches_counts]
                CHECK
                (
                    ([expected_record_count] IS NULL OR [expected_record_count] >= 0)
                    AND [received_record_count] >= 0
                    AND [accepted_record_count] >= 0
                    AND [rejected_record_count] >= 0
                    AND [duplicate_record_count] >= 0
                    AND
                    (
                        [accepted_record_count]
                        + [rejected_record_count]
                        + [duplicate_record_count]
                    ) <= [received_record_count]
                ),
            CONSTRAINT [ck_ingestion_batches_completion]
                CHECK
                (
                    ([status] = 'STARTED' AND [completed_at_utc] IS NULL)
                    OR
                    ([status] <> 'STARTED'
                        AND [completed_at_utc] IS NOT NULL
                        AND [completed_at_utc] >= [started_at_utc])
                )
        );

        CREATE UNIQUE INDEX [ux_ingestion_batches_source_request]
            ON [market].[ingestion_batches] ([data_source_id], [source_request_id])
            WHERE [source_request_id] IS NOT NULL;

        CREATE INDEX [ix_ingestion_batches_lookup]
            ON [market].[ingestion_batches]
            ([data_source_id], [data_type], [timeframe], [started_at_utc] DESC)
            INCLUDE ([status], [instrument_id], [completed_at_utc]);

        CREATE INDEX [ix_ingestion_batches_status]
            ON [market].[ingestion_batches]
            ([status], [started_at_utc] DESC);
    END;

    IF OBJECT_ID(N'[market].[source_observations]', N'U') IS NULL
    BEGIN
        CREATE TABLE [market].[source_observations]
        (
            [source_observation_id] bigint IDENTITY(1,1) NOT NULL,
            [source_observation_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_source_observations_uid] DEFAULT NEWSEQUENTIALID(),
            [ingestion_batch_id] bigint NOT NULL,
            [data_source_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [broker_instrument_mapping_id] bigint NULL,
            [trading_session_id] bigint NULL,
            [data_type] varchar(50) NOT NULL,
            [timeframe] varchar(20) NULL,
            [source_event_id] varchar(200) NULL,
            [source_sequence] varchar(100) NULL,
            [event_at_utc] datetime2(7) NULL,
            [published_at_utc] datetime2(7) NULL,
            [received_at_utc] datetime2(7) NOT NULL,
            [processed_at_utc] datetime2(7) NOT NULL,
            [trade_date] date NULL,
            [revision] int NOT NULL
                CONSTRAINT [df_source_observations_revision] DEFAULT (0),
            [source_version] varchar(100) NULL,
            [payload_contract_version] varchar(50) NULL,
            [payload_hash] char(64) NOT NULL,
            [raw_payload_json] nvarchar(max) NULL,
            [quality_status] varchar(30) NOT NULL,
            [quality_reason_codes_json] nvarchar(max) NULL,
            [is_point_in_time_eligible] bit NOT NULL,
            [correlation_id] uniqueidentifier NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_source_observations_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_source_observations]
                PRIMARY KEY CLUSTERED ([source_observation_id]),
            CONSTRAINT [uq_source_observations_uid]
                UNIQUE ([source_observation_uid]),
            CONSTRAINT [uq_source_observations_identity]
                UNIQUE ([source_observation_id], [instrument_id], [data_source_id]),
            CONSTRAINT [fk_source_observations_batch_source]
                FOREIGN KEY ([ingestion_batch_id], [data_source_id])
                REFERENCES [market].[ingestion_batches]
                    ([ingestion_batch_id], [data_source_id]),
            CONSTRAINT [fk_source_observations_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_source_observations_mapping]
                FOREIGN KEY ([broker_instrument_mapping_id])
                REFERENCES [reference].[broker_instrument_mappings]
                    ([broker_instrument_mapping_id]),
            CONSTRAINT [fk_source_observations_session]
                FOREIGN KEY ([trading_session_id])
                REFERENCES [reference].[trading_sessions] ([trading_session_id]),
            CONSTRAINT [ck_source_observations_data_type]
                CHECK ([data_type] IN ('CANDLE', 'QUOTE', 'TRADE', 'OPEN_INTEREST', 'REFERENCE', 'BROKER_STATE')),
            CONSTRAINT [ck_source_observations_timeframe]
                CHECK
                (
                    ([timeframe] IS NULL OR [timeframe] IN ('1m', '5m', '15m', '1h', '1d'))
                    AND ([data_type] <> 'CANDLE' OR [timeframe] IS NOT NULL)
                ),
            CONSTRAINT [ck_source_observations_revision]
                CHECK ([revision] >= 0),
            CONSTRAINT [ck_source_observations_processing_time]
                CHECK ([processed_at_utc] >= [received_at_utc]),
            CONSTRAINT [ck_source_observations_payload_hash]
                CHECK
                (
                    LEN(RTRIM([payload_hash])) = 64
                    AND [payload_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                ),
            CONSTRAINT [ck_source_observations_raw_payload_json]
                CHECK ([raw_payload_json] IS NULL OR ISJSON([raw_payload_json]) = 1),
            CONSTRAINT [ck_source_observations_reason_codes_json]
                CHECK
                (
                    [quality_reason_codes_json] IS NULL
                    OR ISJSON([quality_reason_codes_json]) = 1
                ),
            CONSTRAINT [ck_source_observations_quality_status]
                CHECK
                (
                    [quality_status] IN
                    (
                        'VALID', 'DEGRADED', 'STALE', 'INCOMPLETE',
                        'DUPLICATE', 'OUT_OF_ORDER', 'CONFLICTED', 'INVALID', 'UNKNOWN'
                    )
                )
        );

        CREATE UNIQUE INDEX [ux_source_observations_source_event_revision]
            ON [market].[source_observations]
            ([data_source_id], [source_event_id], [revision])
            WHERE [source_event_id] IS NOT NULL;

        CREATE INDEX [ix_source_observations_point_in_time]
            ON [market].[source_observations]
            (
                [instrument_id],
                [data_type],
                [timeframe],
                [event_at_utc],
                [received_at_utc],
                [revision]
            )
            INCLUDE ([quality_status], [is_point_in_time_eligible]);

        CREATE INDEX [ix_source_observations_payload_hash]
            ON [market].[source_observations]
            ([data_source_id], [payload_hash], [received_at_utc]);

        CREATE INDEX [ix_source_observations_batch]
            ON [market].[source_observations]
            ([ingestion_batch_id], [source_observation_id]);
    END;

    IF OBJECT_ID(N'[market].[candles]', N'U') IS NULL
    BEGIN
        CREATE TABLE [market].[candles]
        (
            [candle_id] bigint IDENTITY(1,1) NOT NULL,
            [candle_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_candles_uid] DEFAULT NEWSEQUENTIALID(),
            [source_observation_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [data_source_id] bigint NOT NULL,
            [trading_session_id] bigint NOT NULL,
            [trade_date] date NOT NULL,
            [timeframe] varchar(20) NOT NULL,
            [open_at_utc] datetime2(7) NOT NULL,
            [close_at_utc] datetime2(7) NOT NULL,
            [open_price] decimal(19,6) NOT NULL,
            [high_price] decimal(19,6) NOT NULL,
            [low_price] decimal(19,6) NOT NULL,
            [close_price] decimal(19,6) NOT NULL,
            [volume_qty] decimal(19,6) NOT NULL,
            [trade_count] bigint NULL,
            [vwap_price] decimal(19,6) NULL,
            [is_closed] bit NOT NULL,
            [is_provisional] bit NOT NULL
                CONSTRAINT [df_candles_provisional] DEFAULT (0),
            [revision] int NOT NULL
                CONSTRAINT [df_candles_revision] DEFAULT (0),
            [supersedes_candle_id] bigint NULL,
            [is_current] bit NOT NULL
                CONSTRAINT [df_candles_current] DEFAULT (1),
            [source_version] varchar(100) NULL,
            [published_at_utc] datetime2(7) NULL,
            [received_at_utc] datetime2(7) NOT NULL,
            [processed_at_utc] datetime2(7) NOT NULL,
            [quality_status] varchar(30) NOT NULL,
            [quality_reason_codes_json] nvarchar(max) NULL,
            [freshness_policy_version] varchar(100) NULL,
            [is_point_in_time_eligible] bit NOT NULL,
            [is_usable_for_new_exposure] bit NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_candles_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_candles]
                PRIMARY KEY CLUSTERED ([candle_id]),
            CONSTRAINT [uq_candles_uid]
                UNIQUE ([candle_uid]),
            CONSTRAINT [uq_candles_source_observation]
                UNIQUE ([source_observation_id]),
            CONSTRAINT [uq_candles_revision]
                UNIQUE
                (
                    [instrument_id],
                    [data_source_id],
                    [timeframe],
                    [open_at_utc],
                    [revision]
                ),
            CONSTRAINT [fk_candles_source_observation]
                FOREIGN KEY
                (
                    [source_observation_id],
                    [instrument_id],
                    [data_source_id]
                )
                REFERENCES [market].[source_observations]
                (
                    [source_observation_id],
                    [instrument_id],
                    [data_source_id]
                ),
            CONSTRAINT [fk_candles_session]
                FOREIGN KEY ([trading_session_id])
                REFERENCES [reference].[trading_sessions] ([trading_session_id]),
            CONSTRAINT [fk_candles_supersedes]
                FOREIGN KEY ([supersedes_candle_id])
                REFERENCES [market].[candles] ([candle_id]),
            CONSTRAINT [ck_candles_timeframe]
                CHECK ([timeframe] IN ('1m', '5m', '15m', '1h', '1d')),
            CONSTRAINT [ck_candles_time_window]
                CHECK
                (
                    [close_at_utc] > [open_at_utc]
                    AND [processed_at_utc] >= [received_at_utc]
                ),
            CONSTRAINT [ck_candles_ohlc]
                CHECK
                (
                    [open_price] > 0
                    AND [high_price] > 0
                    AND [low_price] > 0
                    AND [close_price] > 0
                    AND [high_price] >= [low_price]
                    AND [high_price] >= [open_price]
                    AND [high_price] >= [close_price]
                    AND [low_price] <= [open_price]
                    AND [low_price] <= [close_price]
                ),
            CONSTRAINT [ck_candles_volume]
                CHECK
                (
                    [volume_qty] >= 0
                    AND ([trade_count] IS NULL OR [trade_count] >= 0)
                    AND ([vwap_price] IS NULL OR [vwap_price] > 0)
                ),
            CONSTRAINT [ck_candles_closed_provisional]
                CHECK
                (
                    ([is_closed] = 1 AND [is_provisional] = 0)
                    OR
                    ([is_closed] = 0 AND [is_provisional] = 1)
                ),
            CONSTRAINT [ck_candles_revision_lineage]
                CHECK
                (
                    ([revision] = 0 AND [supersedes_candle_id] IS NULL)
                    OR
                    ([revision] > 0 AND [supersedes_candle_id] IS NOT NULL)
                ),
            CONSTRAINT [ck_candles_quality_status]
                CHECK
                (
                    [quality_status] IN
                    (
                        'VALID', 'DEGRADED', 'STALE', 'INCOMPLETE',
                        'DUPLICATE', 'OUT_OF_ORDER', 'CONFLICTED', 'INVALID', 'UNKNOWN'
                    )
                ),
            CONSTRAINT [ck_candles_reason_codes_json]
                CHECK
                (
                    [quality_reason_codes_json] IS NULL
                    OR ISJSON([quality_reason_codes_json]) = 1
                ),
            CONSTRAINT [ck_candles_new_exposure_eligibility]
                CHECK
                (
                    [is_usable_for_new_exposure] = 0
                    OR
                    (
                        [is_closed] = 1
                        AND [is_provisional] = 0
                        AND [is_point_in_time_eligible] = 1
                        AND [quality_status] = 'VALID'
                    )
                )
        );

        CREATE UNIQUE INDEX [ux_candles_current_revision]
            ON [market].[candles]
            ([instrument_id], [data_source_id], [timeframe], [open_at_utc])
            WHERE [is_current] = 1;

        CREATE INDEX [ix_candles_latest_closed]
            ON [market].[candles]
            ([instrument_id], [timeframe], [close_at_utc] DESC)
            INCLUDE
            (
                [open_price], [high_price], [low_price], [close_price],
                [volume_qty], [quality_status], [is_current],
                [is_usable_for_new_exposure]
            );

        CREATE INDEX [ix_candles_point_in_time]
            ON [market].[candles]
            (
                [instrument_id],
                [timeframe],
                [received_at_utc],
                [revision]
            )
            INCLUDE
            (
                [open_at_utc], [close_at_utc], [is_current],
                [is_point_in_time_eligible], [quality_status]
            );
    END;

    IF OBJECT_ID(N'[market].[ingestion_cursors]', N'U') IS NULL
    BEGIN
        CREATE TABLE [market].[ingestion_cursors]
        (
            [ingestion_cursor_id] bigint IDENTITY(1,1) NOT NULL,
            [data_source_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [data_type] varchar(50) NOT NULL,
            [timeframe] varchar(20) NULL,
            [cursor_state] varchar(20) NOT NULL,
            [last_source_event_id] varchar(200) NULL,
            [last_source_sequence] varchar(100) NULL,
            [last_event_at_utc] datetime2(7) NULL,
            [last_received_at_utc] datetime2(7) NULL,
            [last_successful_batch_id] bigint NULL,
            [consecutive_failure_count] int NOT NULL
                CONSTRAINT [df_ingestion_cursors_failure_count] DEFAULT (0),
            [last_error_code] varchar(100) NULL,
            [last_error_message] nvarchar(4000) NULL,
            [next_retry_at_utc] datetime2(7) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_ingestion_cursors_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_ingestion_cursors_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_ingestion_cursors]
                PRIMARY KEY CLUSTERED ([ingestion_cursor_id]),
            CONSTRAINT [uq_ingestion_cursors_scope]
                UNIQUE ([data_source_id], [instrument_id], [data_type], [timeframe]),
            CONSTRAINT [fk_ingestion_cursors_source]
                FOREIGN KEY ([data_source_id])
                REFERENCES [market].[data_sources] ([data_source_id]),
            CONSTRAINT [fk_ingestion_cursors_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_ingestion_cursors_batch]
                FOREIGN KEY ([last_successful_batch_id])
                REFERENCES [market].[ingestion_batches] ([ingestion_batch_id]),
            CONSTRAINT [ck_ingestion_cursors_data_type]
                CHECK ([data_type] IN ('CANDLE', 'QUOTE', 'TRADE', 'OPEN_INTEREST', 'REFERENCE', 'BROKER_STATE')),
            CONSTRAINT [ck_ingestion_cursors_timeframe]
                CHECK
                (
                    ([timeframe] IS NULL OR [timeframe] IN ('1m', '5m', '15m', '1h', '1d'))
                    AND ([data_type] <> 'CANDLE' OR [timeframe] IS NOT NULL)
                ),
            CONSTRAINT [ck_ingestion_cursors_state]
                CHECK ([cursor_state] IN ('HEALTHY', 'DEGRADED', 'STALE', 'FAILED', 'PAUSED')),
            CONSTRAINT [ck_ingestion_cursors_failure_count]
                CHECK ([consecutive_failure_count] >= 0)
        );

        CREATE INDEX [ix_ingestion_cursors_health]
            ON [market].[ingestion_cursors]
            ([cursor_state], [updated_at_utc], [next_retry_at_utc]);
    END;

    IF OBJECT_ID(N'[market].[data_quality_assessments]', N'U') IS NULL
    BEGIN
        CREATE TABLE [market].[data_quality_assessments]
        (
            [data_quality_assessment_id] bigint IDENTITY(1,1) NOT NULL,
            [assessment_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_data_quality_assessments_uid] DEFAULT NEWSEQUENTIALID(),
            [contract_version] varchar(20) NOT NULL
                CONSTRAINT [df_data_quality_assessments_contract_version] DEFAULT ('1.0.0'),
            [environment] varchar(20) NOT NULL,
            [instrument_id] bigint NOT NULL,
            [data_type] varchar(50) NOT NULL,
            [timeframe] varchar(20) NULL,
            [data_source_id] bigint NOT NULL,
            [source_version] varchar(100) NULL,
            [source_observation_id] bigint NULL,
            [candle_id] bigint NULL,
            [evaluated_at_utc] datetime2(7) NOT NULL,
            [freshness_basis_utc] datetime2(7) NOT NULL,
            [age_milliseconds] bigint NOT NULL,
            [maximum_age_milliseconds] bigint NOT NULL,
            [quality_status] varchar(30) NOT NULL,
            [reason_codes_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_data_quality_assessments_reasons] DEFAULT (N'[]'),
            [is_usable_for_new_exposure] bit NOT NULL,
            [is_usable_for_exit] bit NOT NULL
                CONSTRAINT [df_data_quality_assessments_exit] DEFAULT (0),
            [revision] int NULL,
            [sequence_gap_count] bigint NULL,
            [policy_version] varchar(100) NOT NULL,
            [correlation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_data_quality_assessments_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_data_quality_assessments]
                PRIMARY KEY CLUSTERED ([data_quality_assessment_id]),
            CONSTRAINT [uq_data_quality_assessments_uid]
                UNIQUE ([assessment_uid]),
            CONSTRAINT [fk_data_quality_assessments_source]
                FOREIGN KEY ([data_source_id])
                REFERENCES [market].[data_sources] ([data_source_id]),
            CONSTRAINT [fk_data_quality_assessments_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_data_quality_assessments_observation]
                FOREIGN KEY ([source_observation_id])
                REFERENCES [market].[source_observations] ([source_observation_id]),
            CONSTRAINT [fk_data_quality_assessments_candle]
                FOREIGN KEY ([candle_id])
                REFERENCES [market].[candles] ([candle_id]),
            CONSTRAINT [ck_data_quality_assessments_contract_version]
                CHECK ([contract_version] = '1.0.0'),
            CONSTRAINT [ck_data_quality_assessments_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_data_quality_assessments_data_type]
                CHECK ([data_type] IN ('CANDLE', 'QUOTE', 'TRADE', 'OPEN_INTEREST', 'REFERENCE', 'BROKER_STATE')),
            CONSTRAINT [ck_data_quality_assessments_timeframe]
                CHECK
                (
                    ([timeframe] IS NULL OR [timeframe] IN ('1m', '5m', '15m', '1h', '1d'))
                    AND ([data_type] <> 'CANDLE' OR [timeframe] IS NOT NULL)
                ),
            CONSTRAINT [ck_data_quality_assessments_age]
                CHECK
                (
                    [age_milliseconds] >= 0
                    AND [maximum_age_milliseconds] >= 0
                ),
            CONSTRAINT [ck_data_quality_assessments_quality_status]
                CHECK
                (
                    [quality_status] IN
                    (
                        'VALID', 'DEGRADED', 'STALE', 'INCOMPLETE',
                        'DUPLICATE', 'OUT_OF_ORDER', 'CONFLICTED', 'INVALID', 'UNKNOWN'
                    )
                ),
            CONSTRAINT [ck_data_quality_assessments_reason_codes_json]
                CHECK (ISJSON([reason_codes_json]) = 1),
            CONSTRAINT [ck_data_quality_assessments_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_data_quality_assessments_revision]
                CHECK ([revision] IS NULL OR [revision] >= 0),
            CONSTRAINT [ck_data_quality_assessments_sequence_gap]
                CHECK ([sequence_gap_count] IS NULL OR [sequence_gap_count] >= 0)
        );

        CREATE INDEX [ix_data_quality_assessments_latest]
            ON [market].[data_quality_assessments]
            (
                [environment],
                [instrument_id],
                [data_type],
                [timeframe],
                [evaluated_at_utc] DESC
            )
            INCLUDE
            (
                [quality_status], [is_usable_for_new_exposure],
                [is_usable_for_exit], [policy_version], [age_milliseconds],
                [maximum_age_milliseconds]
            );

        CREATE INDEX [ix_data_quality_assessments_blocked]
            ON [market].[data_quality_assessments]
            ([environment], [evaluated_at_utc] DESC, [instrument_id])
            INCLUDE ([data_type], [timeframe], [quality_status], [policy_version])
            WHERE [is_usable_for_new_exposure] = 0;

        CREATE INDEX [ix_data_quality_assessments_observation]
            ON [market].[data_quality_assessments]
            ([source_observation_id], [candle_id], [evaluated_at_utc] DESC);
    END;

    UPDATE [operations].[database_metadata]
    SET
        [schema_baseline_version] = 'V0003',
        [updated_at_utc] = SYSUTCDATETIME(),
        [updated_by] = COALESCE(SUSER_SNAME(), N'UNKNOWN')
    WHERE [database_metadata_id] = 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
