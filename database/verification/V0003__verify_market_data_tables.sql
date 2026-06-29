/*
Verification: V0003__verify_market_data_tables.sql
Purpose:
  Verify V0003 market-data tables, foreign keys, indexes, trusted check constraints,
  precision-sensitive columns and the database baseline marker.
Expected result:
  One PASS result set and no raised verification error.
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

DECLARE @expected_tables TABLE
(
    [table_name] sysname NOT NULL PRIMARY KEY
);

INSERT INTO @expected_tables ([table_name])
VALUES
    (N'data_sources'),
    (N'ingestion_batches'),
    (N'source_observations'),
    (N'candles'),
    (N'ingestion_cursors'),
    (N'data_quality_assessments');

IF EXISTS
(
    SELECT 1
    FROM @expected_tables AS expected
    WHERE OBJECT_ID(N'[market].' + QUOTENAME(expected.[table_name]), N'U') IS NULL
)
BEGIN
    SELECT expected.[table_name] AS [missing_table]
    FROM @expected_tables AS expected
    WHERE OBJECT_ID(N'[market].' + QUOTENAME(expected.[table_name]), N'U') IS NULL;

    RAISERROR('V0003 market table verification failed.', 16, 1);
    RETURN;
END;

DECLARE @expected_foreign_keys TABLE
(
    [table_name] nvarchar(300) NOT NULL,
    [foreign_key_name] sysname NOT NULL,
    PRIMARY KEY ([table_name], [foreign_key_name])
);

INSERT INTO @expected_foreign_keys ([table_name], [foreign_key_name])
VALUES
    (N'[market].[ingestion_batches]', N'fk_ingestion_batches_source'),
    (N'[market].[ingestion_batches]', N'fk_ingestion_batches_instrument'),
    (N'[market].[source_observations]', N'fk_source_observations_batch_source'),
    (N'[market].[source_observations]', N'fk_source_observations_instrument'),
    (N'[market].[source_observations]', N'fk_source_observations_mapping'),
    (N'[market].[source_observations]', N'fk_source_observations_session'),
    (N'[market].[candles]', N'fk_candles_source_observation'),
    (N'[market].[candles]', N'fk_candles_session'),
    (N'[market].[candles]', N'fk_candles_supersedes'),
    (N'[market].[ingestion_cursors]', N'fk_ingestion_cursors_source'),
    (N'[market].[ingestion_cursors]', N'fk_ingestion_cursors_instrument'),
    (N'[market].[ingestion_cursors]', N'fk_ingestion_cursors_batch'),
    (N'[market].[data_quality_assessments]', N'fk_data_quality_assessments_source'),
    (N'[market].[data_quality_assessments]', N'fk_data_quality_assessments_instrument'),
    (N'[market].[data_quality_assessments]', N'fk_data_quality_assessments_observation'),
    (N'[market].[data_quality_assessments]', N'fk_data_quality_assessments_candle');

IF EXISTS
(
    SELECT 1
    FROM @expected_foreign_keys AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[foreign_key_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    )
)
BEGIN
    SELECT
        expected.[table_name],
        expected.[foreign_key_name] AS [missing_or_untrusted_foreign_key]
    FROM @expected_foreign_keys AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[foreign_key_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    );

    RAISERROR('V0003 foreign-key verification failed.', 16, 1);
    RETURN;
END;

DECLARE @expected_indexes TABLE
(
    [table_name] nvarchar(300) NOT NULL,
    [index_name] sysname NOT NULL,
    [must_be_unique] bit NOT NULL,
    [must_be_filtered] bit NOT NULL,
    PRIMARY KEY ([table_name], [index_name])
);

INSERT INTO @expected_indexes
(
    [table_name],
    [index_name],
    [must_be_unique],
    [must_be_filtered]
)
VALUES
    (N'[market].[data_sources]', N'ix_data_sources_active', 0, 0),
    (N'[market].[ingestion_batches]', N'ux_ingestion_batches_source_request', 1, 1),
    (N'[market].[ingestion_batches]', N'ix_ingestion_batches_lookup', 0, 0),
    (N'[market].[ingestion_batches]', N'ix_ingestion_batches_status', 0, 0),
    (N'[market].[source_observations]', N'ux_source_observations_source_event_revision', 1, 1),
    (N'[market].[source_observations]', N'ix_source_observations_point_in_time', 0, 0),
    (N'[market].[source_observations]', N'ix_source_observations_payload_hash', 0, 0),
    (N'[market].[source_observations]', N'ix_source_observations_batch', 0, 0),
    (N'[market].[candles]', N'ux_candles_current_revision', 1, 1),
    (N'[market].[candles]', N'ix_candles_latest_closed', 0, 0),
    (N'[market].[candles]', N'ix_candles_point_in_time', 0, 0),
    (N'[market].[ingestion_cursors]', N'ix_ingestion_cursors_health', 0, 0),
    (N'[market].[data_quality_assessments]', N'ix_data_quality_assessments_latest', 0, 0),
    (N'[market].[data_quality_assessments]', N'ix_data_quality_assessments_blocked', 0, 1),
    (N'[market].[data_quality_assessments]', N'ix_data_quality_assessments_observation', 0, 0);

IF EXISTS
(
    SELECT 1
    FROM @expected_indexes AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS actual
        WHERE actual.[object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[index_name]
          AND actual.[is_unique] = expected.[must_be_unique]
          AND actual.[has_filter] = expected.[must_be_filtered]
          AND actual.[is_disabled] = 0
    )
)
BEGIN
    SELECT
        expected.[table_name],
        expected.[index_name] AS [missing_or_invalid_index]
    FROM @expected_indexes AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS actual
        WHERE actual.[object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[index_name]
          AND actual.[is_unique] = expected.[must_be_unique]
          AND actual.[has_filter] = expected.[must_be_filtered]
          AND actual.[is_disabled] = 0
    );

    RAISERROR('V0003 index verification failed.', 16, 1);
    RETURN;
END;

DECLARE @expected_checks TABLE
(
    [table_name] nvarchar(300) NOT NULL,
    [constraint_name] sysname NOT NULL,
    PRIMARY KEY ([table_name], [constraint_name])
);

INSERT INTO @expected_checks ([table_name], [constraint_name])
VALUES
    (N'[market].[ingestion_batches]', N'ck_ingestion_batches_timeframe'),
    (N'[market].[ingestion_batches]', N'ck_ingestion_batches_counts'),
    (N'[market].[ingestion_batches]', N'ck_ingestion_batches_completion'),
    (N'[market].[source_observations]', N'ck_source_observations_timeframe'),
    (N'[market].[source_observations]', N'ck_source_observations_processing_time'),
    (N'[market].[source_observations]', N'ck_source_observations_payload_hash'),
    (N'[market].[source_observations]', N'ck_source_observations_raw_payload_json'),
    (N'[market].[source_observations]', N'ck_source_observations_quality_status'),
    (N'[market].[candles]', N'ck_candles_timeframe'),
    (N'[market].[candles]', N'ck_candles_ohlc'),
    (N'[market].[candles]', N'ck_candles_volume'),
    (N'[market].[candles]', N'ck_candles_closed_provisional'),
    (N'[market].[candles]', N'ck_candles_revision_lineage'),
    (N'[market].[candles]', N'ck_candles_new_exposure_eligibility'),
    (N'[market].[ingestion_cursors]', N'ck_ingestion_cursors_timeframe'),
    (N'[market].[ingestion_cursors]', N'ck_ingestion_cursors_state'),
    (N'[market].[data_quality_assessments]', N'ck_data_quality_assessments_contract_version'),
    (N'[market].[data_quality_assessments]', N'ck_data_quality_assessments_age'),
    (N'[market].[data_quality_assessments]', N'ck_data_quality_assessments_quality_status'),
    (N'[market].[data_quality_assessments]', N'ck_data_quality_assessments_reason_codes_json');

IF EXISTS
(
    SELECT 1
    FROM @expected_checks AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[constraint_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    )
)
BEGIN
    SELECT
        expected.[table_name],
        expected.[constraint_name] AS [missing_or_untrusted_check]
    FROM @expected_checks AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[constraint_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    );

    RAISERROR('V0003 check-constraint verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[market].[candles]')
      AND columns.[name] IN
      (
          N'open_price', N'high_price', N'low_price',
          N'close_price', N'volume_qty', N'vwap_price'
      )
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 6
       AND MIN(CASE WHEN types.[name] = N'decimal' AND columns.[precision] = 19 AND columns.[scale] = 6 THEN 1 ELSE 0 END) = 1
)
BEGIN
    RAISERROR('V0003 candle precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [operations].[database_metadata]
    WHERE [database_metadata_id] = 1
      AND [schema_baseline_version] = 'V0003'
)
BEGIN
    RAISERROR('Database metadata was not advanced to V0003.', 16, 1);
    RETURN;
END;

SELECT
    'PASS' AS [verification_status],
    'V0003' AS [migration_version],
    DB_NAME() AS [database_name],
    (SELECT COUNT_BIG(*) FROM @expected_tables) AS [verified_table_count],
    (SELECT COUNT_BIG(*) FROM @expected_foreign_keys) AS [verified_foreign_key_count],
    (SELECT COUNT_BIG(*) FROM @expected_indexes) AS [verified_index_count],
    (SELECT COUNT_BIG(*) FROM @expected_checks) AS [verified_check_count];
