/*
Verification: V0004__verify_intelligence_and_signal_tables.sql
Purpose:
  Verify V0004 intelligence and signal tables, trusted relationships, filtered indexes,
  contract checks, numeric precision and database baseline metadata.
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
    (N'engines'),
    (N'engine_runs'),
    (N'engine_outputs'),
    (N'engine_output_market_inputs'),
    (N'engine_output_features'),
    (N'engine_output_evidence'),
    (N'engine_output_warnings'),
    (N'signals'),
    (N'signal_engine_outputs'),
    (N'signal_confirmation_timeframes'),
    (N'signal_evidence'),
    (N'signal_status_events');

IF EXISTS
(
    SELECT 1
    FROM @expected_tables AS expected
    WHERE OBJECT_ID(N'[intelligence].' + QUOTENAME(expected.[table_name]), N'U') IS NULL
)
BEGIN
    SELECT expected.[table_name] AS [missing_table]
    FROM @expected_tables AS expected
    WHERE OBJECT_ID(N'[intelligence].' + QUOTENAME(expected.[table_name]), N'U') IS NULL;

    RAISERROR('V0004 intelligence table verification failed.', 16, 1);
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
    (N'[intelligence].[engine_runs]', N'fk_engine_runs_engine'),
    (N'[intelligence].[engine_outputs]', N'fk_engine_outputs_run'),
    (N'[intelligence].[engine_outputs]', N'fk_engine_outputs_engine'),
    (N'[intelligence].[engine_outputs]', N'fk_engine_outputs_instrument'),
    (N'[intelligence].[engine_outputs]', N'fk_engine_outputs_supersedes'),
    (N'[intelligence].[engine_output_market_inputs]', N'fk_engine_output_market_inputs_output'),
    (N'[intelligence].[engine_output_market_inputs]', N'fk_engine_output_market_inputs_candle'),
    (N'[intelligence].[engine_output_market_inputs]', N'fk_engine_output_market_inputs_quality'),
    (N'[intelligence].[engine_output_market_inputs]', N'fk_engine_output_market_inputs_observation'),
    (N'[intelligence].[engine_output_features]', N'fk_engine_output_features_output'),
    (N'[intelligence].[engine_output_evidence]', N'fk_engine_output_evidence_output'),
    (N'[intelligence].[engine_output_warnings]', N'fk_engine_output_warnings_output'),
    (N'[intelligence].[signals]', N'fk_signals_creator_engine'),
    (N'[intelligence].[signals]', N'fk_signals_instrument'),
    (N'[intelligence].[signals]', N'fk_signals_supersedes'),
    (N'[intelligence].[signal_engine_outputs]', N'fk_signal_engine_outputs_signal'),
    (N'[intelligence].[signal_engine_outputs]', N'fk_signal_engine_outputs_output'),
    (N'[intelligence].[signal_confirmation_timeframes]', N'fk_signal_confirmation_timeframes_signal'),
    (N'[intelligence].[signal_evidence]', N'fk_signal_evidence_signal'),
    (N'[intelligence].[signal_status_events]', N'fk_signal_status_events_signal');

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

    RAISERROR('V0004 foreign-key verification failed.', 16, 1);
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
    [table_name], [index_name], [must_be_unique], [must_be_filtered]
)
VALUES
    (N'[intelligence].[engines]', N'ix_engines_active_role', 0, 0),
    (N'[intelligence].[engine_runs]', N'ix_engine_runs_engine_started', 0, 0),
    (N'[intelligence].[engine_runs]', N'ix_engine_runs_status', 0, 0),
    (N'[intelligence].[engine_outputs]', N'ux_engine_outputs_current', 1, 1),
    (N'[intelligence].[engine_outputs]', N'ix_engine_outputs_latest', 0, 0),
    (N'[intelligence].[engine_outputs]', N'ix_engine_outputs_run', 0, 0),
    (N'[intelligence].[engine_outputs]', N'ix_engine_outputs_correlation', 0, 0),
    (N'[intelligence].[engine_output_market_inputs]', N'ux_engine_output_market_inputs_candle', 1, 1),
    (N'[intelligence].[engine_output_market_inputs]', N'ux_engine_output_market_inputs_quality', 1, 1),
    (N'[intelligence].[engine_output_market_inputs]', N'ux_engine_output_market_inputs_observation', 1, 1),
    (N'[intelligence].[signals]', N'ix_signals_latest', 0, 0),
    (N'[intelligence].[signals]', N'ix_signals_strategy', 0, 0),
    (N'[intelligence].[signals]', N'ix_signals_correlation', 0, 0),
    (N'[intelligence].[signal_engine_outputs]', N'ix_signal_engine_outputs_output', 0, 0),
    (N'[intelligence].[signal_status_events]', N'ix_signal_status_events_latest', 0, 0),
    (N'[intelligence].[signal_status_events]', N'ix_signal_status_events_status', 0, 0);

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

    RAISERROR('V0004 index verification failed.', 16, 1);
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
    (N'[intelligence].[engines]', N'ck_engines_role'),
    (N'[intelligence].[engines]', N'ck_engines_signal_authority'),
    (N'[intelligence].[engines]', N'ck_engines_no_execution_authority'),
    (N'[intelligence].[engine_runs]', N'ck_engine_runs_completion'),
    (N'[intelligence].[engine_runs]', N'ck_engine_runs_data_cutoff'),
    (N'[intelligence].[engine_outputs]', N'ck_engine_outputs_contract_version'),
    (N'[intelligence].[engine_outputs]', N'ck_engine_outputs_direction'),
    (N'[intelligence].[engine_outputs]', N'ck_engine_outputs_score'),
    (N'[intelligence].[engine_outputs]', N'ck_engine_outputs_confidence'),
    (N'[intelligence].[engine_outputs]', N'ck_engine_outputs_time_order'),
    (N'[intelligence].[engine_outputs]', N'ck_engine_outputs_revision_lineage'),
    (N'[intelligence].[engine_outputs]', N'ck_engine_outputs_fusion_eligibility'),
    (N'[intelligence].[engine_outputs]', N'ck_engine_outputs_raw_contract_json'),
    (N'[intelligence].[engine_output_market_inputs]', N'ck_engine_output_market_inputs_exactly_one_reference'),
    (N'[intelligence].[engine_output_evidence]', N'ck_engine_output_evidence_impact'),
    (N'[intelligence].[signals]', N'ck_signals_contract_version'),
    (N'[intelligence].[signals]', N'ck_signals_direction'),
    (N'[intelligence].[signals]', N'ck_signals_entry_window'),
    (N'[intelligence].[signals]', N'ck_signals_initial_status'),
    (N'[intelligence].[signals]', N'ck_signals_validity'),
    (N'[intelligence].[signals]', N'ck_signals_raw_contract_json'),
    (N'[intelligence].[signal_engine_outputs]', N'ck_signal_engine_outputs_role'),
    (N'[intelligence].[signal_confirmation_timeframes]', N'ck_signal_confirmation_timeframes_timeframe'),
    (N'[intelligence].[signal_evidence]', N'ck_signal_evidence_impact'),
    (N'[intelligence].[signal_status_events]', N'ck_signal_status_events_status'),
    (N'[intelligence].[signal_status_events]', N'ck_signal_status_events_reasons_json');

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

    RAISERROR('V0004 check-constraint verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
      AND columns.[name] IN (N'score', N'confidence', N'data_completeness')
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 3
       AND MIN
       (
           CASE
               WHEN types.[name] = N'decimal'
                AND columns.[precision] = 9
                AND columns.[scale] = 8
               THEN 1 ELSE 0
           END
       ) = 1
)
BEGIN
    RAISERROR('V0004 engine-output precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[intelligence].[signals]')
      AND columns.[name] IN
      (
          N'reference_price', N'minimum_price', N'maximum_price', N'invalidation_price'
      )
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 4
       AND MIN
       (
           CASE
               WHEN types.[name] = N'decimal'
                AND columns.[precision] = 19
                AND columns.[scale] = 6
               THEN 1 ELSE 0
           END
       ) = 1
)
BEGIN
    RAISERROR('V0004 signal-price precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [operations].[database_metadata]
    WHERE [database_metadata_id] = 1
      AND [schema_baseline_version] = 'V0004'
)
BEGIN
    RAISERROR('Database metadata was not advanced to V0004.', 16, 1);
    RETURN;
END;

SELECT
    'PASS' AS [verification_status],
    'V0004' AS [migration_version],
    DB_NAME() AS [database_name],
    (SELECT COUNT_BIG(*) FROM @expected_tables) AS [verified_table_count],
    (SELECT COUNT_BIG(*) FROM @expected_foreign_keys) AS [verified_foreign_key_count],
    (SELECT COUNT_BIG(*) FROM @expected_indexes) AS [verified_index_count],
    (SELECT COUNT_BIG(*) FROM @expected_checks) AS [verified_check_count];
