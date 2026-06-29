/*
Migration: V0005__create_thesis_tables.sql
Purpose:
  Create immutable trade-thesis storage with exact signal and regime lineage,
  normalized evidence, assumptions, scenarios, invalidation definitions/events,
  append-only status history and governed failure fingerprints.
Dependencies:
  V0001__create_schemas_and_migration_metadata.sql
  V0002__create_reference_tables.sql
  V0003__create_market_data_tables.sql
  V0004__create_intelligence_and_signal_tables.sql
Expected runtime impact:
  Additive DDL only. No existing intelligence data is scanned or backfilled.
Locking considerations:
  Schema modification locks are acquired while tables, constraints and indexes are created.
Backward-compatibility window:
  Fully additive.
Data migration requirements:
  None.
Verification script:
  database/verification/V0005__verify_thesis_tables.sql
Recovery plan:
  Roll forward with a later migration. Destructive rollback is limited to disposable local databases.
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

    IF SCHEMA_ID(N'thesis') IS NULL
        THROW 55001, 'V0001 is required: schema thesis does not exist.', 1;

    IF OBJECT_ID(N'[reference].[instruments]', N'U') IS NULL
        THROW 55002, 'V0002 is required: reference.instruments does not exist.', 1;

    IF OBJECT_ID(N'[market].[candles]', N'U') IS NULL
        THROW 55003, 'V0003 is required: market.candles does not exist.', 1;

    IF OBJECT_ID(N'[intelligence].[signals]', N'U') IS NULL
        THROW 55004, 'V0004 is required: intelligence.signals does not exist.', 1;

    IF OBJECT_ID(N'[intelligence].[engine_outputs]', N'U') IS NULL
        THROW 55005, 'V0004 is required: intelligence.engine_outputs does not exist.', 1;

    IF OBJECT_ID(N'[thesis].[theses]', N'U') IS NULL
    BEGIN
        CREATE TABLE [thesis].[theses]
        (
            [thesis_id] bigint IDENTITY(1,1) NOT NULL,
            [thesis_uid] uniqueidentifier NOT NULL,
            [message_uid] uniqueidentifier NOT NULL,
            [signal_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [market_regime_engine_output_id] bigint NOT NULL,
            [contract_version] varchar(20) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [thesis_version] int NOT NULL,
            [market_regime_code] varchar(100) NOT NULL,
            [market_regime_confidence] decimal(9,8) NOT NULL,
            [market_regime_as_of_utc] datetime2(7) NOT NULL,
            [primary_hypothesis] nvarchar(4000) NOT NULL,
            [confidence] decimal(9,8) NOT NULL,
            [initial_status] varchar(20) NOT NULL,
            [status_reasons_json] nvarchar(max) NULL,
            [generated_at_utc] datetime2(7) NOT NULL,
            [valid_until_utc] datetime2(7) NOT NULL,
            [supersedes_thesis_uid] uniqueidentifier NULL,
            [is_current] bit NOT NULL
                CONSTRAINT [df_theses_is_current] DEFAULT (1),
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [raw_contract_json] nvarchar(max) NOT NULL,
            [contract_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_theses_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_theses]
                PRIMARY KEY CLUSTERED ([thesis_id]),
            CONSTRAINT [uq_theses_uid]
                UNIQUE ([thesis_uid]),
            CONSTRAINT [uq_theses_message_uid]
                UNIQUE ([message_uid]),
            CONSTRAINT [uq_theses_id_instrument]
                UNIQUE ([thesis_id], [instrument_id]),
            CONSTRAINT [uq_theses_signal_version]
                UNIQUE ([signal_id], [thesis_version]),
            CONSTRAINT [fk_theses_signal]
                FOREIGN KEY ([signal_id], [instrument_id])
                REFERENCES [intelligence].[signals] ([signal_id], [instrument_id]),
            CONSTRAINT [fk_theses_regime_output]
                FOREIGN KEY ([market_regime_engine_output_id], [instrument_id])
                REFERENCES [intelligence].[engine_outputs]
                    ([engine_output_id], [instrument_id]),
            CONSTRAINT [fk_theses_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_theses_supersedes]
                FOREIGN KEY ([supersedes_thesis_uid])
                REFERENCES [thesis].[theses] ([thesis_uid]),
            CONSTRAINT [ck_theses_contract_version]
                CHECK ([contract_version] = '1.0.0'),
            CONSTRAINT [ck_theses_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_theses_version]
                CHECK ([thesis_version] >= 1),
            CONSTRAINT [ck_theses_regime_confidence]
                CHECK ([market_regime_confidence] BETWEEN 0 AND 1),
            CONSTRAINT [ck_theses_confidence]
                CHECK ([confidence] BETWEEN 0 AND 1),
            CONSTRAINT [ck_theses_initial_status]
                CHECK ([initial_status] IN ('DRAFT', 'VALIDATED', 'REJECTED', 'EXPIRED', 'SUPERSEDED')),
            CONSTRAINT [ck_theses_validity]
                CHECK
                (
                    [market_regime_as_of_utc] <= [generated_at_utc]
                    AND [valid_until_utc] > [generated_at_utc]
                ),
            CONSTRAINT [ck_theses_version_lineage]
                CHECK
                (
                    ([thesis_version] = 1 AND [supersedes_thesis_uid] IS NULL)
                    OR
                    ([thesis_version] > 1 AND [supersedes_thesis_uid] IS NOT NULL)
                ),
            CONSTRAINT [ck_theses_not_self_superseding]
                CHECK
                (
                    [supersedes_thesis_uid] IS NULL
                    OR [supersedes_thesis_uid] <> [thesis_uid]
                ),
            CONSTRAINT [ck_theses_status_reasons_json]
                CHECK ([status_reasons_json] IS NULL OR ISJSON([status_reasons_json]) = 1),
            CONSTRAINT [ck_theses_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_theses_raw_contract_json]
                CHECK (ISJSON([raw_contract_json]) = 1),
            CONSTRAINT [ck_theses_contract_hash]
                CHECK
                (
                    LEN(RTRIM([contract_hash])) = 64
                    AND [contract_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE UNIQUE INDEX [ux_theses_current_signal]
            ON [thesis].[theses] ([signal_id])
            WHERE [is_current] = 1;

        CREATE INDEX [ix_theses_latest_instrument]
            ON [thesis].[theses]
            ([instrument_id], [generated_at_utc] DESC)
            INCLUDE
            (
                [signal_id], [thesis_version], [market_regime_code],
                [confidence], [initial_status], [valid_until_utc], [is_current]
            );

        CREATE INDEX [ix_theses_correlation]
            ON [thesis].[theses] ([correlation_id], [generated_at_utc]);
    END;

    IF OBJECT_ID(N'[thesis].[thesis_signal_relationships]', N'U') IS NULL
    BEGIN
        CREATE TABLE [thesis].[thesis_signal_relationships]
        (
            [thesis_signal_relationship_id] bigint IDENTITY(1,1) NOT NULL,
            [thesis_id] bigint NOT NULL,
            [signal_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [relationship_role] varchar(30) NOT NULL,
            [relationship_weight] decimal(9,8) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_thesis_signal_relationships_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_thesis_signal_relationships]
                PRIMARY KEY CLUSTERED ([thesis_signal_relationship_id]),
            CONSTRAINT [uq_thesis_signal_relationships]
                UNIQUE ([thesis_id], [signal_id], [relationship_role]),
            CONSTRAINT [fk_thesis_signal_relationships_thesis]
                FOREIGN KEY ([thesis_id], [instrument_id])
                REFERENCES [thesis].[theses] ([thesis_id], [instrument_id]),
            CONSTRAINT [fk_thesis_signal_relationships_signal]
                FOREIGN KEY ([signal_id], [instrument_id])
                REFERENCES [intelligence].[signals] ([signal_id], [instrument_id]),
            CONSTRAINT [ck_thesis_signal_relationships_role]
                CHECK
                (
                    [relationship_role] IN
                    ('PRIMARY', 'SUPPORTING', 'CONTRADICTING', 'CONTEXT')
                ),
            CONSTRAINT [ck_thesis_signal_relationships_weight]
                CHECK
                (
                    [relationship_weight] IS NULL
                    OR [relationship_weight] BETWEEN 0 AND 1
                )
        );

        CREATE UNIQUE INDEX [ux_thesis_signal_relationships_primary]
            ON [thesis].[thesis_signal_relationships] ([thesis_id])
            WHERE [relationship_role] = 'PRIMARY';

        CREATE INDEX [ix_thesis_signal_relationships_signal]
            ON [thesis].[thesis_signal_relationships]
            ([signal_id], [relationship_role], [thesis_id]);
    END;

    IF OBJECT_ID(N'[thesis].[thesis_evidence]', N'U') IS NULL
    BEGIN
        CREATE TABLE [thesis].[thesis_evidence]
        (
            [thesis_evidence_id] bigint IDENTITY(1,1) NOT NULL,
            [thesis_id] bigint NOT NULL,
            [evidence_code] varchar(100) NOT NULL,
            [evidence_description] nvarchar(1000) NOT NULL,
            [source_type] varchar(30) NOT NULL,
            [impact] varchar(20) NOT NULL,
            [weight] decimal(9,8) NULL,
            [engine_output_id] bigint NULL,
            [market_candle_id] bigint NULL,
            [data_quality_assessment_id] bigint NULL,
            [source_signal_id] bigint NULL,
            [policy_reference] varchar(200) NULL,
            [other_reference] varchar(200) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_thesis_evidence_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_thesis_evidence]
                PRIMARY KEY CLUSTERED ([thesis_evidence_id]),
            CONSTRAINT [uq_thesis_evidence_code]
                UNIQUE ([thesis_id], [evidence_code]),
            CONSTRAINT [fk_thesis_evidence_thesis]
                FOREIGN KEY ([thesis_id])
                REFERENCES [thesis].[theses] ([thesis_id]),
            CONSTRAINT [fk_thesis_evidence_engine_output]
                FOREIGN KEY ([engine_output_id])
                REFERENCES [intelligence].[engine_outputs] ([engine_output_id]),
            CONSTRAINT [fk_thesis_evidence_market_candle]
                FOREIGN KEY ([market_candle_id])
                REFERENCES [market].[candles] ([candle_id]),
            CONSTRAINT [fk_thesis_evidence_quality]
                FOREIGN KEY ([data_quality_assessment_id])
                REFERENCES [market].[data_quality_assessments] ([data_quality_assessment_id]),
            CONSTRAINT [fk_thesis_evidence_source_signal]
                FOREIGN KEY ([source_signal_id])
                REFERENCES [intelligence].[signals] ([signal_id]),
            CONSTRAINT [ck_thesis_evidence_source_type]
                CHECK
                (
                    [source_type] IN
                    ('ENGINE_OUTPUT', 'MARKET_SNAPSHOT', 'SIGNAL', 'POLICY', 'OTHER')
                ),
            CONSTRAINT [ck_thesis_evidence_impact]
                CHECK ([impact] IN ('SUPPORTS', 'CONTRADICTS', 'NEUTRAL')),
            CONSTRAINT [ck_thesis_evidence_weight]
                CHECK ([weight] IS NULL OR [weight] BETWEEN 0 AND 1),
            CONSTRAINT [ck_thesis_evidence_source_reference]
                CHECK
                (
                    ([source_type] = 'ENGINE_OUTPUT'
                        AND [engine_output_id] IS NOT NULL
                        AND [market_candle_id] IS NULL
                        AND [data_quality_assessment_id] IS NULL
                        AND [source_signal_id] IS NULL
                        AND [policy_reference] IS NULL
                        AND [other_reference] IS NULL)
                    OR
                    ([source_type] = 'MARKET_SNAPSHOT'
                        AND
                        (
                            (CASE WHEN [market_candle_id] IS NULL THEN 0 ELSE 1 END)
                            + (CASE WHEN [data_quality_assessment_id] IS NULL THEN 0 ELSE 1 END)
                        ) = 1
                        AND [engine_output_id] IS NULL
                        AND [source_signal_id] IS NULL
                        AND [policy_reference] IS NULL
                        AND [other_reference] IS NULL)
                    OR
                    ([source_type] = 'SIGNAL'
                        AND [source_signal_id] IS NOT NULL
                        AND [engine_output_id] IS NULL
                        AND [market_candle_id] IS NULL
                        AND [data_quality_assessment_id] IS NULL
                        AND [policy_reference] IS NULL
                        AND [other_reference] IS NULL)
                    OR
                    ([source_type] = 'POLICY'
                        AND [policy_reference] IS NOT NULL
                        AND [engine_output_id] IS NULL
                        AND [market_candle_id] IS NULL
                        AND [data_quality_assessment_id] IS NULL
                        AND [source_signal_id] IS NULL
                        AND [other_reference] IS NULL)
                    OR
                    ([source_type] = 'OTHER'
                        AND [other_reference] IS NOT NULL
                        AND [engine_output_id] IS NULL
                        AND [market_candle_id] IS NULL
                        AND [data_quality_assessment_id] IS NULL
                        AND [source_signal_id] IS NULL
                        AND [policy_reference] IS NULL)
                )
        );

        CREATE INDEX [ix_thesis_evidence_source]
            ON [thesis].[thesis_evidence]
            ([source_type], [impact], [thesis_id]);
    END;

    IF OBJECT_ID(N'[thesis].[thesis_assumptions]', N'U') IS NULL
    BEGIN
        CREATE TABLE [thesis].[thesis_assumptions]
        (
            [thesis_assumption_id] bigint IDENTITY(1,1) NOT NULL,
            [thesis_id] bigint NOT NULL,
            [assumption_sequence] int NOT NULL,
            [assumption_text] nvarchar(1000) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_thesis_assumptions_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_thesis_assumptions]
                PRIMARY KEY CLUSTERED ([thesis_assumption_id]),
            CONSTRAINT [uq_thesis_assumptions_sequence]
                UNIQUE ([thesis_id], [assumption_sequence]),
            CONSTRAINT [fk_thesis_assumptions_thesis]
                FOREIGN KEY ([thesis_id])
                REFERENCES [thesis].[theses] ([thesis_id]),
            CONSTRAINT [ck_thesis_assumptions_sequence]
                CHECK ([assumption_sequence] >= 1)
        );
    END;

    IF OBJECT_ID(N'[thesis].[thesis_invalidation_conditions]', N'U') IS NULL
    BEGIN
        CREATE TABLE [thesis].[thesis_invalidation_conditions]
        (
            [thesis_invalidation_condition_id] bigint IDENTITY(1,1) NOT NULL,
            [thesis_id] bigint NOT NULL,
            [condition_code] varchar(100) NOT NULL,
            [condition_type] varchar(30) NOT NULL,
            [condition_description] nvarchar(1000) NOT NULL,
            [severity] varchar(20) NOT NULL,
            [price_level] decimal(19,6) NULL,
            [threshold_value] decimal(19,8) NULL,
            [threshold_unit] varchar(30) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_thesis_invalidation_conditions_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_thesis_invalidation_conditions]
                PRIMARY KEY CLUSTERED ([thesis_invalidation_condition_id]),
            CONSTRAINT [uq_thesis_invalidation_conditions_code]
                UNIQUE ([thesis_id], [condition_code]),
            CONSTRAINT [fk_thesis_invalidation_conditions_thesis]
                FOREIGN KEY ([thesis_id])
                REFERENCES [thesis].[theses] ([thesis_id]),
            CONSTRAINT [ck_thesis_invalidation_conditions_type]
                CHECK
                (
                    [condition_type] IN
                    ('PRICE', 'TIME', 'VOLATILITY', 'REGIME', 'DATA_QUALITY', 'OTHER')
                ),
            CONSTRAINT [ck_thesis_invalidation_conditions_severity]
                CHECK ([severity] IN ('WARNING', 'INVALIDATES')),
            CONSTRAINT [ck_thesis_invalidation_conditions_price]
                CHECK ([price_level] IS NULL OR [price_level] > 0),
            CONSTRAINT [ck_thesis_invalidation_conditions_threshold]
                CHECK
                (
                    ([threshold_value] IS NULL AND [threshold_unit] IS NULL)
                    OR
                    ([threshold_value] IS NOT NULL AND [threshold_unit] IS NOT NULL)
                )
        );
    END;

    IF OBJECT_ID(N'[thesis].[thesis_invalidation_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [thesis].[thesis_invalidation_events]
        (
            [thesis_invalidation_event_id] bigint IDENTITY(1,1) NOT NULL,
            [thesis_invalidation_event_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_thesis_invalidation_events_uid] DEFAULT NEWSEQUENTIALID(),
            [thesis_id] bigint NOT NULL,
            [thesis_invalidation_condition_id] bigint NOT NULL,
            [event_sequence] int NOT NULL,
            [event_state] varchar(20) NOT NULL,
            [observed_at_utc] datetime2(7) NOT NULL,
            [observed_value] decimal(19,8) NULL,
            [observed_text] nvarchar(1000) NULL,
            [market_candle_id] bigint NULL,
            [engine_output_id] bigint NULL,
            [data_quality_assessment_id] bigint NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_thesis_invalidation_events_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_thesis_invalidation_events]
                PRIMARY KEY CLUSTERED ([thesis_invalidation_event_id]),
            CONSTRAINT [uq_thesis_invalidation_events_uid]
                UNIQUE ([thesis_invalidation_event_uid]),
            CONSTRAINT [uq_thesis_invalidation_events_sequence]
                UNIQUE ([thesis_id], [event_sequence]),
            CONSTRAINT [fk_thesis_invalidation_events_thesis]
                FOREIGN KEY ([thesis_id])
                REFERENCES [thesis].[theses] ([thesis_id]),
            CONSTRAINT [fk_thesis_invalidation_events_condition]
                FOREIGN KEY ([thesis_invalidation_condition_id])
                REFERENCES [thesis].[thesis_invalidation_conditions]
                    ([thesis_invalidation_condition_id]),
            CONSTRAINT [fk_thesis_invalidation_events_candle]
                FOREIGN KEY ([market_candle_id])
                REFERENCES [market].[candles] ([candle_id]),
            CONSTRAINT [fk_thesis_invalidation_events_output]
                FOREIGN KEY ([engine_output_id])
                REFERENCES [intelligence].[engine_outputs] ([engine_output_id]),
            CONSTRAINT [fk_thesis_invalidation_events_quality]
                FOREIGN KEY ([data_quality_assessment_id])
                REFERENCES [market].[data_quality_assessments] ([data_quality_assessment_id]),
            CONSTRAINT [ck_thesis_invalidation_events_sequence]
                CHECK ([event_sequence] >= 1),
            CONSTRAINT [ck_thesis_invalidation_events_state]
                CHECK ([event_state] IN ('OBSERVED', 'WARNING', 'TRIGGERED', 'CLEARED')),
            CONSTRAINT [ck_thesis_invalidation_events_observation]
                CHECK
                (
                    [observed_value] IS NOT NULL
                    OR [observed_text] IS NOT NULL
                    OR [market_candle_id] IS NOT NULL
                    OR [engine_output_id] IS NOT NULL
                    OR [data_quality_assessment_id] IS NOT NULL
                ),
            CONSTRAINT [ck_thesis_invalidation_events_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE INDEX [ix_thesis_invalidation_events_latest]
            ON [thesis].[thesis_invalidation_events]
            ([thesis_id], [event_sequence] DESC)
            INCLUDE ([event_state], [observed_at_utc], [thesis_invalidation_condition_id]);

        CREATE INDEX [ix_thesis_invalidation_events_triggered]
            ON [thesis].[thesis_invalidation_events]
            ([event_state], [observed_at_utc] DESC, [thesis_id]);
    END;

    IF OBJECT_ID(N'[thesis].[thesis_scenarios]', N'U') IS NULL
    BEGIN
        CREATE TABLE [thesis].[thesis_scenarios]
        (
            [thesis_scenario_id] bigint IDENTITY(1,1) NOT NULL,
            [thesis_id] bigint NOT NULL,
            [scenario_name] varchar(100) NOT NULL,
            [scenario_description] nvarchar(2000) NOT NULL,
            [probability] decimal(9,8) NOT NULL,
            [is_primary] bit NOT NULL,
            [confirmation_deadline_utc] datetime2(7) NULL,
            [expected_path_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_thesis_scenarios_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_thesis_scenarios]
                PRIMARY KEY CLUSTERED ([thesis_scenario_id]),
            CONSTRAINT [uq_thesis_scenarios_name]
                UNIQUE ([thesis_id], [scenario_name]),
            CONSTRAINT [fk_thesis_scenarios_thesis]
                FOREIGN KEY ([thesis_id])
                REFERENCES [thesis].[theses] ([thesis_id]),
            CONSTRAINT [ck_thesis_scenarios_probability]
                CHECK ([probability] BETWEEN 0 AND 1),
            CONSTRAINT [ck_thesis_scenarios_expected_path_json]
                CHECK ([expected_path_json] IS NULL OR ISJSON([expected_path_json]) = 1)
        );

        CREATE UNIQUE INDEX [ux_thesis_scenarios_primary]
            ON [thesis].[thesis_scenarios] ([thesis_id])
            WHERE [is_primary] = 1;
    END;

    IF OBJECT_ID(N'[thesis].[thesis_status_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [thesis].[thesis_status_events]
        (
            [thesis_status_event_id] bigint IDENTITY(1,1) NOT NULL,
            [thesis_status_event_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_thesis_status_events_uid] DEFAULT NEWSEQUENTIALID(),
            [thesis_id] bigint NOT NULL,
            [event_sequence] int NOT NULL,
            [status] varchar(20) NOT NULL,
            [reason_codes_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_thesis_status_events_reasons] DEFAULT (N'[]'),
            [occurred_at_utc] datetime2(7) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_thesis_status_events_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_thesis_status_events]
                PRIMARY KEY CLUSTERED ([thesis_status_event_id]),
            CONSTRAINT [uq_thesis_status_events_uid]
                UNIQUE ([thesis_status_event_uid]),
            CONSTRAINT [uq_thesis_status_events_sequence]
                UNIQUE ([thesis_id], [event_sequence]),
            CONSTRAINT [fk_thesis_status_events_thesis]
                FOREIGN KEY ([thesis_id])
                REFERENCES [thesis].[theses] ([thesis_id]),
            CONSTRAINT [ck_thesis_status_events_sequence]
                CHECK ([event_sequence] >= 0),
            CONSTRAINT [ck_thesis_status_events_status]
                CHECK ([status] IN ('DRAFT', 'VALIDATED', 'REJECTED', 'EXPIRED', 'SUPERSEDED')),
            CONSTRAINT [ck_thesis_status_events_reasons_json]
                CHECK (ISJSON([reason_codes_json]) = 1),
            CONSTRAINT [ck_thesis_status_events_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE INDEX [ix_thesis_status_events_latest]
            ON [thesis].[thesis_status_events]
            ([thesis_id], [event_sequence] DESC)
            INCLUDE ([status], [occurred_at_utc]);

        CREATE INDEX [ix_thesis_status_events_status]
            ON [thesis].[thesis_status_events]
            ([status], [occurred_at_utc] DESC);
    END;

    IF OBJECT_ID(N'[thesis].[thesis_failure_fingerprints]', N'U') IS NULL
    BEGIN
        CREATE TABLE [thesis].[thesis_failure_fingerprints]
        (
            [thesis_failure_fingerprint_id] bigint IDENTITY(1,1) NOT NULL,
            [failure_fingerprint_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_thesis_failure_fingerprints_uid] DEFAULT NEWSEQUENTIALID(),
            [thesis_id] bigint NOT NULL,
            [fingerprint_code] varchar(100) NOT NULL,
            [failure_category] varchar(40) NOT NULL,
            [severity] varchar(20) NOT NULL,
            [description] nvarchar(2000) NOT NULL,
            [evidence_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_thesis_failure_fingerprints_evidence] DEFAULT (N'[]'),
            [observed_at_utc] datetime2(7) NOT NULL,
            [learning_disposition] varchar(30) NOT NULL,
            [linked_learning_candidate_uid] uniqueidentifier NULL,
            [production_change_authorized] bit NOT NULL
                CONSTRAINT [df_thesis_failure_fingerprints_production_change] DEFAULT (0),
            [correlation_id] uniqueidentifier NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_thesis_failure_fingerprints_created_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_thesis_failure_fingerprints]
                PRIMARY KEY CLUSTERED ([thesis_failure_fingerprint_id]),
            CONSTRAINT [uq_thesis_failure_fingerprints_uid]
                UNIQUE ([failure_fingerprint_uid]),
            CONSTRAINT [uq_thesis_failure_fingerprints_code]
                UNIQUE ([thesis_id], [fingerprint_code]),
            CONSTRAINT [fk_thesis_failure_fingerprints_thesis]
                FOREIGN KEY ([thesis_id])
                REFERENCES [thesis].[theses] ([thesis_id]),
            CONSTRAINT [ck_thesis_failure_fingerprints_category]
                CHECK
                (
                    [failure_category] IN
                    (
                        'FAILED_SCENARIO',
                        'WRONG_ASSUMPTION',
                        'MISSED_SIGNAL',
                        'REGIME_TRANSITION_FAILURE',
                        'INCORRECT_WEIGHTING',
                        'STOP_LOSS_HIT',
                        'TIMEOUT',
                        'DATA_QUALITY_FAILURE',
                        'OTHER'
                    )
                ),
            CONSTRAINT [ck_thesis_failure_fingerprints_severity]
                CHECK ([severity] IN ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')),
            CONSTRAINT [ck_thesis_failure_fingerprints_disposition]
                CHECK
                (
                    [learning_disposition] IN
                    ('RECORDED', 'REVIEW_PENDING', 'ACCEPTED_FOR_RESEARCH', 'REJECTED')
                ),
            CONSTRAINT [ck_thesis_failure_fingerprints_evidence_json]
                CHECK (ISJSON([evidence_json]) = 1),
            CONSTRAINT [ck_thesis_failure_fingerprints_no_direct_production_change]
                CHECK ([production_change_authorized] = 0)
        );

        CREATE INDEX [ix_thesis_failure_fingerprints_review]
            ON [thesis].[thesis_failure_fingerprints]
            ([learning_disposition], [observed_at_utc] DESC)
            INCLUDE ([thesis_id], [failure_category], [severity]);
    END;

    UPDATE [operations].[database_metadata]
    SET
        [schema_baseline_version] = 'V0005',
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
