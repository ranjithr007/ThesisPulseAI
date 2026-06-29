/*
Verification: V0005__verify_thesis_tables.sql
Purpose:
  Verify V0005 thesis tables, trusted relationships, filtered indexes,
  canonical contract checks, fixed precision and the V0005 baseline marker.
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
    (N'theses'),
    (N'thesis_signal_relationships'),
    (N'thesis_evidence'),
    (N'thesis_assumptions'),
    (N'thesis_invalidation_conditions'),
    (N'thesis_invalidation_events'),
    (N'thesis_scenarios'),
    (N'thesis_status_events'),
    (N'thesis_failure_fingerprints');

IF EXISTS
(
    SELECT 1
    FROM @expected_tables AS expected
    WHERE OBJECT_ID(N'[thesis].' + QUOTENAME(expected.[table_name]), N'U') IS NULL
)
BEGIN
    SELECT expected.[table_name] AS [missing_table]
    FROM @expected_tables AS expected
    WHERE OBJECT_ID(N'[thesis].' + QUOTENAME(expected.[table_name]), N'U') IS NULL;

    RAISERROR('V0005 thesis table verification failed.', 16, 1);
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
    (N'[thesis].[theses]', N'fk_theses_signal'),
    (N'[thesis].[theses]', N'fk_theses_regime_output'),
    (N'[thesis].[theses]', N'fk_theses_instrument'),
    (N'[thesis].[theses]', N'fk_theses_supersedes'),
    (N'[thesis].[thesis_signal_relationships]', N'fk_thesis_signal_relationships_thesis'),
    (N'[thesis].[thesis_signal_relationships]', N'fk_thesis_signal_relationships_signal'),
    (N'[thesis].[thesis_evidence]', N'fk_thesis_evidence_thesis'),
    (N'[thesis].[thesis_evidence]', N'fk_thesis_evidence_engine_output'),
    (N'[thesis].[thesis_evidence]', N'fk_thesis_evidence_market_candle'),
    (N'[thesis].[thesis_evidence]', N'fk_thesis_evidence_quality'),
    (N'[thesis].[thesis_evidence]', N'fk_thesis_evidence_source_signal'),
    (N'[thesis].[thesis_assumptions]', N'fk_thesis_assumptions_thesis'),
    (N'[thesis].[thesis_invalidation_conditions]', N'fk_thesis_invalidation_conditions_thesis'),
    (N'[thesis].[thesis_invalidation_events]', N'fk_thesis_invalidation_events_thesis'),
    (N'[thesis].[thesis_invalidation_events]', N'fk_thesis_invalidation_events_condition'),
    (N'[thesis].[thesis_invalidation_events]', N'fk_thesis_invalidation_events_candle'),
    (N'[thesis].[thesis_invalidation_events]', N'fk_thesis_invalidation_events_output'),
    (N'[thesis].[thesis_invalidation_events]', N'fk_thesis_invalidation_events_quality'),
    (N'[thesis].[thesis_scenarios]', N'fk_thesis_scenarios_thesis'),
    (N'[thesis].[thesis_status_events]', N'fk_thesis_status_events_thesis'),
    (N'[thesis].[thesis_failure_fingerprints]', N'fk_thesis_failure_fingerprints_thesis');

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

    RAISERROR('V0005 foreign-key verification failed.', 16, 1);
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
    (N'[thesis].[theses]', N'ux_theses_current_signal', 1, 1),
    (N'[thesis].[theses]', N'ix_theses_latest_instrument', 0, 0),
    (N'[thesis].[theses]', N'ix_theses_correlation', 0, 0),
    (N'[thesis].[thesis_signal_relationships]', N'ux_thesis_signal_relationships_primary', 1, 1),
    (N'[thesis].[thesis_signal_relationships]', N'ix_thesis_signal_relationships_signal', 0, 0),
    (N'[thesis].[thesis_evidence]', N'ix_thesis_evidence_source', 0, 0),
    (N'[thesis].[thesis_invalidation_events]', N'ix_thesis_invalidation_events_latest', 0, 0),
    (N'[thesis].[thesis_invalidation_events]', N'ix_thesis_invalidation_events_triggered', 0, 0),
    (N'[thesis].[thesis_scenarios]', N'ux_thesis_scenarios_primary', 1, 1),
    (N'[thesis].[thesis_status_events]', N'ix_thesis_status_events_latest', 0, 0),
    (N'[thesis].[thesis_status_events]', N'ix_thesis_status_events_status', 0, 0),
    (N'[thesis].[thesis_failure_fingerprints]', N'ix_thesis_failure_fingerprints_review', 0, 0);

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

    RAISERROR('V0005 index verification failed.', 16, 1);
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
    (N'[thesis].[theses]', N'ck_theses_contract_version'),
    (N'[thesis].[theses]', N'ck_theses_environment'),
    (N'[thesis].[theses]', N'ck_theses_version'),
    (N'[thesis].[theses]', N'ck_theses_regime_confidence'),
    (N'[thesis].[theses]', N'ck_theses_confidence'),
    (N'[thesis].[theses]', N'ck_theses_initial_status'),
    (N'[thesis].[theses]', N'ck_theses_validity'),
    (N'[thesis].[theses]', N'ck_theses_version_lineage'),
    (N'[thesis].[theses]', N'ck_theses_raw_contract_json'),
    (N'[thesis].[thesis_signal_relationships]', N'ck_thesis_signal_relationships_role'),
    (N'[thesis].[thesis_signal_relationships]', N'ck_thesis_signal_relationships_weight'),
    (N'[thesis].[thesis_evidence]', N'ck_thesis_evidence_source_type'),
    (N'[thesis].[thesis_evidence]', N'ck_thesis_evidence_impact'),
    (N'[thesis].[thesis_evidence]', N'ck_thesis_evidence_source_reference'),
    (N'[thesis].[thesis_assumptions]', N'ck_thesis_assumptions_sequence'),
    (N'[thesis].[thesis_invalidation_conditions]', N'ck_thesis_invalidation_conditions_type'),
    (N'[thesis].[thesis_invalidation_conditions]', N'ck_thesis_invalidation_conditions_severity'),
    (N'[thesis].[thesis_invalidation_conditions]', N'ck_thesis_invalidation_conditions_threshold'),
    (N'[thesis].[thesis_invalidation_events]', N'ck_thesis_invalidation_events_state'),
    (N'[thesis].[thesis_invalidation_events]', N'ck_thesis_invalidation_events_observation'),
    (N'[thesis].[thesis_scenarios]', N'ck_thesis_scenarios_probability'),
    (N'[thesis].[thesis_status_events]', N'ck_thesis_status_events_status'),
    (N'[thesis].[thesis_status_events]', N'ck_thesis_status_events_reasons_json'),
    (N'[thesis].[thesis_failure_fingerprints]', N'ck_thesis_failure_fingerprints_category'),
    (N'[thesis].[thesis_failure_fingerprints]', N'ck_thesis_failure_fingerprints_disposition'),
    (N'[thesis].[thesis_failure_fingerprints]', N'ck_thesis_failure_fingerprints_no_direct_production_change');

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

    RAISERROR('V0005 check-constraint verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[thesis].[theses]')
      AND columns.[name] IN (N'market_regime_confidence', N'confidence')
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 2
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
    RAISERROR('V0005 thesis confidence precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[thesis].[thesis_invalidation_conditions]')
      AND columns.[name] = N'price_level'
      AND types.[name] = N'decimal'
      AND columns.[precision] = 19
      AND columns.[scale] = 6
)
BEGIN
    RAISERROR('V0005 invalidation price precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [operations].[database_metadata]
    WHERE [database_metadata_id] = 1
      AND [schema_baseline_version] = 'V0005'
)
BEGIN
    RAISERROR('Database metadata was not advanced to V0005.', 16, 1);
    RETURN;
END;

SELECT
    'PASS' AS [verification_status],
    'V0005' AS [migration_version],
    DB_NAME() AS [database_name],
    (SELECT COUNT_BIG(*) FROM @expected_tables) AS [verified_table_count],
    (SELECT COUNT_BIG(*) FROM @expected_foreign_keys) AS [verified_foreign_key_count],
    (SELECT COUNT_BIG(*) FROM @expected_indexes) AS [verified_index_count],
    (SELECT COUNT_BIG(*) FROM @expected_checks) AS [verified_check_count];
