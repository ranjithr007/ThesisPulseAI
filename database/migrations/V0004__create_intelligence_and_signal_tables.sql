/*
Migration: V0004__create_intelligence_and_signal_tables.sql
Purpose:
  Create intelligence-engine registry and run state, immutable canonical engine outputs,
  exact market/quality/feature/evidence lineage, canonical signals and append-only signal status events.
Dependencies:
  V0001__create_schemas_and_migration_metadata.sql
  V0002__create_reference_tables.sql
  V0003__create_market_data_tables.sql
Expected runtime impact:
  Additive DDL only. No existing market data is scanned or backfilled.
Locking considerations:
  Schema modification locks are acquired while tables, constraints and indexes are created.
Backward-compatibility window:
  Fully additive.
Data migration requirements:
  None.
Verification script:
  database/verification/V0004__verify_intelligence_and_signal_tables.sql
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

    IF SCHEMA_ID(N'intelligence') IS NULL
        THROW 54001, 'V0001 is required: schema intelligence does not exist.', 1;

    IF OBJECT_ID(N'[reference].[instruments]', N'U') IS NULL
        THROW 54002, 'V0002 is required: reference.instruments does not exist.', 1;

    IF OBJECT_ID(N'[market].[candles]', N'U') IS NULL
        THROW 54003, 'V0003 is required: market.candles does not exist.', 1;

    IF OBJECT_ID(N'[market].[data_quality_assessments]', N'U') IS NULL
        THROW 54004, 'V0003 is required: market.data_quality_assessments does not exist.', 1;

    IF OBJECT_ID(N'[intelligence].[engines]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[engines]
        (
            [engine_id] bigint IDENTITY(1,1) NOT NULL,
            [engine_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_engines_uid] DEFAULT NEWSEQUENTIALID(),
            [engine_code] varchar(100) NOT NULL,
            [engine_name] nvarchar(200) NOT NULL,
            [engine_role] varchar(30) NOT NULL,
            [owner_service] varchar(100) NOT NULL,
            [can_create_signals] bit NOT NULL
                CONSTRAINT [df_engines_can_create_signals] DEFAULT (0),
            [can_execute_orders] bit NOT NULL
                CONSTRAINT [df_engines_can_execute_orders] DEFAULT (0),
            [is_active] bit NOT NULL
                CONSTRAINT [df_engines_active] DEFAULT (1),
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_engines_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_engines_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_engines]
                PRIMARY KEY CLUSTERED ([engine_id]),
            CONSTRAINT [uq_engines_uid]
                UNIQUE ([engine_uid]),
            CONSTRAINT [uq_engines_code]
                UNIQUE ([engine_code]),
            CONSTRAINT [ck_engines_role]
                CHECK
                (
                    [engine_role] IN
                    (
                        'DIRECTIONAL_VOTER',
                        'META_CONTROLLER',
                        'HARD_GATE',
                        'LEARNING_CONTROLLER',
                        'CONTEXT_PROVIDER',
                        'FUSION'
                    )
                ),
            CONSTRAINT [ck_engines_signal_authority]
                CHECK ([can_create_signals] = 0 OR [engine_role] = 'FUSION'),
            CONSTRAINT [ck_engines_no_execution_authority]
                CHECK ([can_execute_orders] = 0)
        );

        CREATE INDEX [ix_engines_active_role]
            ON [intelligence].[engines] ([is_active], [engine_role], [engine_code]);
    END;

    IF OBJECT_ID(N'[intelligence].[engine_runs]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[engine_runs]
        (
            [engine_run_id] bigint IDENTITY(1,1) NOT NULL,
            [engine_run_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_engine_runs_uid] DEFAULT NEWSEQUENTIALID(),
            [engine_id] bigint NOT NULL,
            [environment] varchar(20) NOT NULL,
            [engine_version] varchar(50) NOT NULL,
            [configuration_version] varchar(100) NOT NULL,
            [feature_set_version] varchar(100) NULL,
            [model_version] varchar(100) NULL,
            [data_cutoff_utc] datetime2(7) NOT NULL,
            [started_at_utc] datetime2(7) NOT NULL,
            [completed_at_utc] datetime2(7) NULL,
            [status] varchar(20) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [input_count] bigint NOT NULL
                CONSTRAINT [df_engine_runs_input_count] DEFAULT (0),
            [output_count] bigint NOT NULL
                CONSTRAINT [df_engine_runs_output_count] DEFAULT (0),
            [warning_count] bigint NOT NULL
                CONSTRAINT [df_engine_runs_warning_count] DEFAULT (0),
            [error_code] varchar(100) NULL,
            [error_message] nvarchar(4000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_engine_runs_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_engine_runs_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_engine_runs]
                PRIMARY KEY CLUSTERED ([engine_run_id]),
            CONSTRAINT [uq_engine_runs_uid]
                UNIQUE ([engine_run_uid]),
            CONSTRAINT [fk_engine_runs_engine]
                FOREIGN KEY ([engine_id])
                REFERENCES [intelligence].[engines] ([engine_id]),
            CONSTRAINT [ck_engine_runs_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_engine_runs_status]
                CHECK ([status] IN ('STARTED', 'SUCCEEDED', 'PARTIAL', 'FAILED', 'CANCELLED')),
            CONSTRAINT [ck_engine_runs_counts]
                CHECK
                (
                    [input_count] >= 0
                    AND [output_count] >= 0
                    AND [warning_count] >= 0
                ),
            CONSTRAINT [ck_engine_runs_completion]
                CHECK
                (
                    ([status] = 'STARTED' AND [completed_at_utc] IS NULL)
                    OR
                    ([status] <> 'STARTED'
                        AND [completed_at_utc] IS NOT NULL
                        AND [completed_at_utc] >= [started_at_utc])
                ),
            CONSTRAINT [ck_engine_runs_data_cutoff]
                CHECK ([data_cutoff_utc] <= [started_at_utc])
        );

        CREATE INDEX [ix_engine_runs_engine_started]
            ON [intelligence].[engine_runs]
            ([engine_id], [started_at_utc] DESC)
            INCLUDE
            (
                [environment], [engine_version], [configuration_version],
                [model_version], [status], [data_cutoff_utc]
            );

        CREATE INDEX [ix_engine_runs_status]
            ON [intelligence].[engine_runs]
            ([status], [started_at_utc] DESC);
    END;

    IF OBJECT_ID(N'[intelligence].[engine_outputs]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[engine_outputs]
        (
            [engine_output_id] bigint IDENTITY(1,1) NOT NULL,
            [engine_output_uid] uniqueidentifier NOT NULL,
            [message_uid] uniqueidentifier NOT NULL,
            [engine_run_id] bigint NOT NULL,
            [engine_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [contract_version] varchar(20) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [engine_name_snapshot] varchar(100) NOT NULL,
            [engine_version] varchar(50) NOT NULL,
            [timeframe] varchar(20) NOT NULL,
            [as_of_utc] datetime2(7) NOT NULL,
            [generated_at_utc] datetime2(7) NOT NULL,
            [expires_at_utc] datetime2(7) NOT NULL,
            [direction] varchar(30) NOT NULL,
            [score] decimal(9,8) NOT NULL,
            [confidence] decimal(9,8) NOT NULL,
            [data_quality_status] varchar(20) NOT NULL,
            [data_completeness] decimal(9,8) NOT NULL,
            [freshness_milliseconds] bigint NOT NULL,
            [missing_fields_json] nvarchar(max) NULL,
            [is_stale] bit NOT NULL,
            [is_eligible_for_fusion] bit NOT NULL,
            [revision] int NOT NULL
                CONSTRAINT [df_engine_outputs_revision] DEFAULT (0),
            [supersedes_engine_output_uid] uniqueidentifier NULL,
            [is_current] bit NOT NULL
                CONSTRAINT [df_engine_outputs_current] DEFAULT (1),
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [raw_contract_json] nvarchar(max) NOT NULL,
            [contract_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_engine_outputs_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_engine_outputs]
                PRIMARY KEY CLUSTERED ([engine_output_id]),
            CONSTRAINT [uq_engine_outputs_uid]
                UNIQUE ([engine_output_uid]),
            CONSTRAINT [uq_engine_outputs_message_uid]
                UNIQUE ([message_uid]),
            CONSTRAINT [uq_engine_outputs_id_instrument]
                UNIQUE ([engine_output_id], [instrument_id]),
            CONSTRAINT [uq_engine_outputs_revision]
                UNIQUE
                (
                    [engine_id],
                    [instrument_id],
                    [timeframe],
                    [as_of_utc],
                    [revision]
                ),
            CONSTRAINT [fk_engine_outputs_run]
                FOREIGN KEY ([engine_run_id])
                REFERENCES [intelligence].[engine_runs] ([engine_run_id]),
            CONSTRAINT [fk_engine_outputs_engine]
                FOREIGN KEY ([engine_id])
                REFERENCES [intelligence].[engines] ([engine_id]),
            CONSTRAINT [fk_engine_outputs_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_engine_outputs_supersedes]
                FOREIGN KEY ([supersedes_engine_output_uid])
                REFERENCES [intelligence].[engine_outputs] ([engine_output_uid]),
            CONSTRAINT [ck_engine_outputs_contract_version]
                CHECK ([contract_version] = '1.0.0'),
            CONSTRAINT [ck_engine_outputs_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_engine_outputs_timeframe]
                CHECK ([timeframe] IN ('1m', '5m', '15m', '1h', '1d')),
            CONSTRAINT [ck_engine_outputs_direction]
                CHECK
                (
                    [direction] IN
                    ('STRONG_LONG', 'LONG', 'NEUTRAL', 'SHORT', 'STRONG_SHORT', 'NO_SIGNAL')
                ),
            CONSTRAINT [ck_engine_outputs_score]
                CHECK ([score] BETWEEN -1 AND 1),
            CONSTRAINT [ck_engine_outputs_confidence]
                CHECK ([confidence] BETWEEN 0 AND 1),
            CONSTRAINT [ck_engine_outputs_data_quality]
                CHECK ([data_quality_status] IN ('VALID', 'DEGRADED', 'INVALID')),
            CONSTRAINT [ck_engine_outputs_completeness]
                CHECK ([data_completeness] BETWEEN 0 AND 1),
            CONSTRAINT [ck_engine_outputs_freshness]
                CHECK ([freshness_milliseconds] >= 0),
            CONSTRAINT [ck_engine_outputs_time_order]
                CHECK
                (
                    [as_of_utc] <= [generated_at_utc]
                    AND [expires_at_utc] > [generated_at_utc]
                ),
            CONSTRAINT [ck_engine_outputs_revision_lineage]
                CHECK
                (
                    ([revision] = 0 AND [supersedes_engine_output_uid] IS NULL)
                    OR
                    ([revision] > 0 AND [supersedes_engine_output_uid] IS NOT NULL)
                ),
            CONSTRAINT [ck_engine_outputs_not_self_superseding]
                CHECK
                (
                    [supersedes_engine_output_uid] IS NULL
                    OR [supersedes_engine_output_uid] <> [engine_output_uid]
                ),
            CONSTRAINT [ck_engine_outputs_fusion_eligibility]
                CHECK
                (
                    [is_eligible_for_fusion] = 0
                    OR
                    (
                        [is_stale] = 0
                        AND [data_quality_status] <> 'INVALID'
                        AND [expires_at_utc] > [generated_at_utc]
                    )
                ),
            CONSTRAINT [ck_engine_outputs_missing_fields_json]
                CHECK ([missing_fields_json] IS NULL OR ISJSON([missing_fields_json]) = 1),
            CONSTRAINT [ck_engine_outputs_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_engine_outputs_raw_contract_json]
                CHECK (ISJSON([raw_contract_json]) = 1),
            CONSTRAINT [ck_engine_outputs_contract_hash]
                CHECK
                (
                    LEN(RTRIM([contract_hash])) = 64
                    AND [contract_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE UNIQUE INDEX [ux_engine_outputs_current]
            ON [intelligence].[engine_outputs]
            ([engine_id], [instrument_id], [timeframe], [as_of_utc])
            WHERE [is_current] = 1;

        CREATE INDEX [ix_engine_outputs_latest]
            ON [intelligence].[engine_outputs]
            ([instrument_id], [timeframe], [generated_at_utc] DESC)
            INCLUDE
            (
                [engine_id], [engine_version], [direction], [score], [confidence],
                [data_quality_status], [is_stale], [is_eligible_for_fusion], [expires_at_utc]
            );

        CREATE INDEX [ix_engine_outputs_run]
            ON [intelligence].[engine_outputs]
            ([engine_run_id], [engine_output_id]);

        CREATE INDEX [ix_engine_outputs_correlation]
            ON [intelligence].[engine_outputs]
            ([correlation_id], [generated_at_utc]);
    END;

    IF OBJECT_ID(N'[intelligence].[engine_output_market_inputs]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[engine_output_market_inputs]
        (
            [engine_output_market_input_id] bigint IDENTITY(1,1) NOT NULL,
            [engine_output_id] bigint NOT NULL,
            [input_role] varchar(30) NOT NULL,
            [candle_id] bigint NULL,
            [data_quality_assessment_id] bigint NULL,
            [source_observation_id] bigint NULL,
            [consumed_at_utc] datetime2(7) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_engine_output_market_inputs_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_engine_output_market_inputs]
                PRIMARY KEY CLUSTERED ([engine_output_market_input_id]),
            CONSTRAINT [fk_engine_output_market_inputs_output]
                FOREIGN KEY ([engine_output_id])
                REFERENCES [intelligence].[engine_outputs] ([engine_output_id]),
            CONSTRAINT [fk_engine_output_market_inputs_candle]
                FOREIGN KEY ([candle_id])
                REFERENCES [market].[candles] ([candle_id]),
            CONSTRAINT [fk_engine_output_market_inputs_quality]
                FOREIGN KEY ([data_quality_assessment_id])
                REFERENCES [market].[data_quality_assessments] ([data_quality_assessment_id]),
            CONSTRAINT [fk_engine_output_market_inputs_observation]
                FOREIGN KEY ([source_observation_id])
                REFERENCES [market].[source_observations] ([source_observation_id]),
            CONSTRAINT [ck_engine_output_market_inputs_role]
                CHECK ([input_role] IN ('PRIMARY', 'CONFIRMATION', 'CONTEXT', 'QUALITY', 'SUBSTITUTE')),
            CONSTRAINT [ck_engine_output_market_inputs_exactly_one_reference]
                CHECK
                (
                    (CASE WHEN [candle_id] IS NULL THEN 0 ELSE 1 END)
                    + (CASE WHEN [data_quality_assessment_id] IS NULL THEN 0 ELSE 1 END)
                    + (CASE WHEN [source_observation_id] IS NULL THEN 0 ELSE 1 END)
                    = 1
                )
        );

        CREATE UNIQUE INDEX [ux_engine_output_market_inputs_candle]
            ON [intelligence].[engine_output_market_inputs]
            ([engine_output_id], [input_role], [candle_id])
            WHERE [candle_id] IS NOT NULL;

        CREATE UNIQUE INDEX [ux_engine_output_market_inputs_quality]
            ON [intelligence].[engine_output_market_inputs]
            ([engine_output_id], [input_role], [data_quality_assessment_id])
            WHERE [data_quality_assessment_id] IS NOT NULL;

        CREATE UNIQUE INDEX [ux_engine_output_market_inputs_observation]
            ON [intelligence].[engine_output_market_inputs]
            ([engine_output_id], [input_role], [source_observation_id])
            WHERE [source_observation_id] IS NOT NULL;
    END;

    IF OBJECT_ID(N'[intelligence].[engine_output_features]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[engine_output_features]
        (
            [engine_output_feature_id] bigint IDENTITY(1,1) NOT NULL,
            [engine_output_id] bigint NOT NULL,
            [feature_name] varchar(200) NOT NULL,
            [feature_version] varchar(100) NOT NULL,
            [feature_value_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_engine_output_features_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_engine_output_features]
                PRIMARY KEY CLUSTERED ([engine_output_feature_id]),
            CONSTRAINT [fk_engine_output_features_output]
                FOREIGN KEY ([engine_output_id])
                REFERENCES [intelligence].[engine_outputs] ([engine_output_id]),
            CONSTRAINT [uq_engine_output_features_name]
                UNIQUE ([engine_output_id], [feature_name], [feature_version]),
            CONSTRAINT [ck_engine_output_features_value_json]
                CHECK ([feature_value_json] IS NULL OR ISJSON([feature_value_json]) = 1)
        );
    END;

    IF OBJECT_ID(N'[intelligence].[engine_output_evidence]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[engine_output_evidence]
        (
            [engine_output_evidence_id] bigint IDENTITY(1,1) NOT NULL,
            [engine_output_id] bigint NOT NULL,
            [evidence_code] varchar(100) NOT NULL,
            [evidence_message] nvarchar(1000) NOT NULL,
            [impact] varchar(30) NOT NULL,
            [weight] decimal(9,8) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_engine_output_evidence_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_engine_output_evidence]
                PRIMARY KEY CLUSTERED ([engine_output_evidence_id]),
            CONSTRAINT [fk_engine_output_evidence_output]
                FOREIGN KEY ([engine_output_id])
                REFERENCES [intelligence].[engine_outputs] ([engine_output_id]),
            CONSTRAINT [uq_engine_output_evidence_code]
                UNIQUE ([engine_output_id], [evidence_code]),
            CONSTRAINT [ck_engine_output_evidence_impact]
                CHECK ([impact] IN ('SUPPORTS_LONG', 'SUPPORTS_SHORT', 'CONTRADICTS', 'NEUTRAL')),
            CONSTRAINT [ck_engine_output_evidence_weight]
                CHECK ([weight] IS NULL OR [weight] BETWEEN 0 AND 1)
        );
    END;

    IF OBJECT_ID(N'[intelligence].[engine_output_warnings]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[engine_output_warnings]
        (
            [engine_output_warning_id] bigint IDENTITY(1,1) NOT NULL,
            [engine_output_id] bigint NOT NULL,
            [warning_code] varchar(100) NOT NULL,
            [warning_message] nvarchar(1000) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_engine_output_warnings_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_engine_output_warnings]
                PRIMARY KEY CLUSTERED ([engine_output_warning_id]),
            CONSTRAINT [fk_engine_output_warnings_output]
                FOREIGN KEY ([engine_output_id])
                REFERENCES [intelligence].[engine_outputs] ([engine_output_id]),
            CONSTRAINT [uq_engine_output_warnings_code]
                UNIQUE ([engine_output_id], [warning_code])
        );
    END;

    IF OBJECT_ID(N'[intelligence].[signals]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[signals]
        (
            [signal_id] bigint IDENTITY(1,1) NOT NULL,
            [signal_uid] uniqueidentifier NOT NULL,
            [message_uid] uniqueidentifier NOT NULL,
            [creator_engine_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [contract_version] varchar(20) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [strategy_code] varchar(100) NOT NULL,
            [strategy_version] varchar(50) NOT NULL,
            [direction] varchar(10) NOT NULL,
            [primary_timeframe] varchar(20) NOT NULL,
            [strength] decimal(9,8) NOT NULL,
            [confidence] decimal(9,8) NOT NULL,
            [entry_opens_at_utc] datetime2(7) NOT NULL,
            [entry_closes_at_utc] datetime2(7) NOT NULL,
            [reference_price] decimal(19,6) NOT NULL,
            [minimum_price] decimal(19,6) NULL,
            [maximum_price] decimal(19,6) NULL,
            [invalidation_price] decimal(19,6) NOT NULL,
            [invalidation_reason] nvarchar(1000) NOT NULL,
            [expected_holding_period_minutes] int NOT NULL,
            [initial_status] varchar(20) NOT NULL,
            [status_reasons_json] nvarchar(max) NULL,
            [generated_at_utc] datetime2(7) NOT NULL,
            [valid_until_utc] datetime2(7) NOT NULL,
            [supersedes_signal_uid] uniqueidentifier NULL,
            [fusion_policy_version] varchar(50) NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [raw_contract_json] nvarchar(max) NOT NULL,
            [contract_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_signals_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_signals]
                PRIMARY KEY CLUSTERED ([signal_id]),
            CONSTRAINT [uq_signals_uid]
                UNIQUE ([signal_uid]),
            CONSTRAINT [uq_signals_message_uid]
                UNIQUE ([message_uid]),
            CONSTRAINT [uq_signals_id_instrument]
                UNIQUE ([signal_id], [instrument_id]),
            CONSTRAINT [fk_signals_creator_engine]
                FOREIGN KEY ([creator_engine_id])
                REFERENCES [intelligence].[engines] ([engine_id]),
            CONSTRAINT [fk_signals_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [fk_signals_supersedes]
                FOREIGN KEY ([supersedes_signal_uid])
                REFERENCES [intelligence].[signals] ([signal_uid]),
            CONSTRAINT [ck_signals_contract_version]
                CHECK ([contract_version] = '1.0.0'),
            CONSTRAINT [ck_signals_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_signals_direction]
                CHECK ([direction] IN ('LONG', 'SHORT')),
            CONSTRAINT [ck_signals_primary_timeframe]
                CHECK ([primary_timeframe] IN ('1m', '5m', '15m', '1h', '1d')),
            CONSTRAINT [ck_signals_strength]
                CHECK ([strength] BETWEEN 0 AND 1),
            CONSTRAINT [ck_signals_confidence]
                CHECK ([confidence] BETWEEN 0 AND 1),
            CONSTRAINT [ck_signals_entry_window]
                CHECK
                (
                    [entry_closes_at_utc] > [entry_opens_at_utc]
                    AND [reference_price] > 0
                    AND ([minimum_price] IS NULL OR [minimum_price] > 0)
                    AND ([maximum_price] IS NULL OR [maximum_price] > 0)
                    AND ([minimum_price] IS NULL OR [reference_price] >= [minimum_price])
                    AND ([maximum_price] IS NULL OR [reference_price] <= [maximum_price])
                    AND
                    (
                        [minimum_price] IS NULL
                        OR [maximum_price] IS NULL
                        OR [maximum_price] >= [minimum_price]
                    )
                ),
            CONSTRAINT [ck_signals_invalidation]
                CHECK ([invalidation_price] > 0),
            CONSTRAINT [ck_signals_holding_period]
                CHECK ([expected_holding_period_minutes] >= 1),
            CONSTRAINT [ck_signals_initial_status]
                CHECK
                (
                    [initial_status] IN
                    ('CANDIDATE', 'VALIDATED', 'REJECTED', 'EXPIRED', 'SUPERSEDED', 'CONSUMED')
                ),
            CONSTRAINT [ck_signals_validity]
                CHECK
                (
                    [valid_until_utc] > [generated_at_utc]
                    AND [entry_closes_at_utc] <= [valid_until_utc]
                ),
            CONSTRAINT [ck_signals_not_self_superseding]
                CHECK
                (
                    [supersedes_signal_uid] IS NULL
                    OR [supersedes_signal_uid] <> [signal_uid]
                ),
            CONSTRAINT [ck_signals_status_reasons_json]
                CHECK ([status_reasons_json] IS NULL OR ISJSON([status_reasons_json]) = 1),
            CONSTRAINT [ck_signals_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_signals_raw_contract_json]
                CHECK (ISJSON([raw_contract_json]) = 1),
            CONSTRAINT [ck_signals_contract_hash]
                CHECK
                (
                    LEN(RTRIM([contract_hash])) = 64
                    AND [contract_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE INDEX [ix_signals_latest]
            ON [intelligence].[signals]
            ([instrument_id], [primary_timeframe], [generated_at_utc] DESC)
            INCLUDE
            (
                [strategy_code], [strategy_version], [direction],
                [strength], [confidence], [initial_status], [valid_until_utc]
            );

        CREATE INDEX [ix_signals_strategy]
            ON [intelligence].[signals]
            ([strategy_code], [strategy_version], [environment], [generated_at_utc] DESC);

        CREATE INDEX [ix_signals_correlation]
            ON [intelligence].[signals]
            ([correlation_id], [generated_at_utc]);
    END;

    IF OBJECT_ID(N'[intelligence].[signal_engine_outputs]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[signal_engine_outputs]
        (
            [signal_engine_output_id] bigint IDENTITY(1,1) NOT NULL,
            [signal_id] bigint NOT NULL,
            [engine_output_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [lineage_role] varchar(30) NOT NULL,
            [effective_weight] decimal(9,8) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_signal_engine_outputs_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_signal_engine_outputs]
                PRIMARY KEY CLUSTERED ([signal_engine_output_id]),
            CONSTRAINT [uq_signal_engine_outputs]
                UNIQUE ([signal_id], [engine_output_id]),
            CONSTRAINT [fk_signal_engine_outputs_signal]
                FOREIGN KEY ([signal_id], [instrument_id])
                REFERENCES [intelligence].[signals] ([signal_id], [instrument_id]),
            CONSTRAINT [fk_signal_engine_outputs_output]
                FOREIGN KEY ([engine_output_id], [instrument_id])
                REFERENCES [intelligence].[engine_outputs]
                    ([engine_output_id], [instrument_id]),
            CONSTRAINT [ck_signal_engine_outputs_role]
                CHECK
                (
                    [lineage_role] IN
                    ('DIRECTIONAL', 'CONTEXT', 'CONTRADICTION', 'HARD_GATE', 'QUALITY', 'FUSION')
                ),
            CONSTRAINT [ck_signal_engine_outputs_weight]
                CHECK ([effective_weight] IS NULL OR [effective_weight] BETWEEN 0 AND 1)
        );

        CREATE INDEX [ix_signal_engine_outputs_output]
            ON [intelligence].[signal_engine_outputs]
            ([engine_output_id], [signal_id]);
    END;

    IF OBJECT_ID(N'[intelligence].[signal_confirmation_timeframes]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[signal_confirmation_timeframes]
        (
            [signal_confirmation_timeframe_id] bigint IDENTITY(1,1) NOT NULL,
            [signal_id] bigint NOT NULL,
            [timeframe] varchar(20) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_signal_confirmation_timeframes_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_signal_confirmation_timeframes]
                PRIMARY KEY CLUSTERED ([signal_confirmation_timeframe_id]),
            CONSTRAINT [fk_signal_confirmation_timeframes_signal]
                FOREIGN KEY ([signal_id])
                REFERENCES [intelligence].[signals] ([signal_id]),
            CONSTRAINT [uq_signal_confirmation_timeframes]
                UNIQUE ([signal_id], [timeframe]),
            CONSTRAINT [ck_signal_confirmation_timeframes_timeframe]
                CHECK ([timeframe] IN ('1m', '5m', '15m', '1h', '1d'))
        );
    END;

    IF OBJECT_ID(N'[intelligence].[signal_evidence]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[signal_evidence]
        (
            [signal_evidence_id] bigint IDENTITY(1,1) NOT NULL,
            [signal_id] bigint NOT NULL,
            [evidence_code] varchar(100) NOT NULL,
            [evidence_message] nvarchar(1000) NOT NULL,
            [impact] varchar(30) NOT NULL,
            [weight] decimal(9,8) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_signal_evidence_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_signal_evidence]
                PRIMARY KEY CLUSTERED ([signal_evidence_id]),
            CONSTRAINT [fk_signal_evidence_signal]
                FOREIGN KEY ([signal_id])
                REFERENCES [intelligence].[signals] ([signal_id]),
            CONSTRAINT [uq_signal_evidence_code]
                UNIQUE ([signal_id], [evidence_code]),
            CONSTRAINT [ck_signal_evidence_impact]
                CHECK ([impact] IN ('SUPPORTS_LONG', 'SUPPORTS_SHORT', 'CONTRADICTS', 'NEUTRAL')),
            CONSTRAINT [ck_signal_evidence_weight]
                CHECK ([weight] IS NULL OR [weight] BETWEEN 0 AND 1)
        );
    END;

    IF OBJECT_ID(N'[intelligence].[signal_status_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [intelligence].[signal_status_events]
        (
            [signal_status_event_id] bigint IDENTITY(1,1) NOT NULL,
            [signal_status_event_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_signal_status_events_uid] DEFAULT NEWSEQUENTIALID(),
            [signal_id] bigint NOT NULL,
            [event_sequence] int NOT NULL,
            [status] varchar(20) NOT NULL,
            [reason_codes_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_signal_status_events_reasons] DEFAULT (N'[]'),
            [occurred_at_utc] datetime2(7) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_signal_status_events_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_signal_status_events]
                PRIMARY KEY CLUSTERED ([signal_status_event_id]),
            CONSTRAINT [uq_signal_status_events_uid]
                UNIQUE ([signal_status_event_uid]),
            CONSTRAINT [uq_signal_status_events_sequence]
                UNIQUE ([signal_id], [event_sequence]),
            CONSTRAINT [fk_signal_status_events_signal]
                FOREIGN KEY ([signal_id])
                REFERENCES [intelligence].[signals] ([signal_id]),
            CONSTRAINT [ck_signal_status_events_sequence]
                CHECK ([event_sequence] >= 0),
            CONSTRAINT [ck_signal_status_events_status]
                CHECK
                (
                    [status] IN
                    ('CANDIDATE', 'VALIDATED', 'REJECTED', 'EXPIRED', 'SUPERSEDED', 'CONSUMED')
                ),
            CONSTRAINT [ck_signal_status_events_reasons_json]
                CHECK (ISJSON([reason_codes_json]) = 1),
            CONSTRAINT [ck_signal_status_events_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE INDEX [ix_signal_status_events_latest]
            ON [intelligence].[signal_status_events]
            ([signal_id], [event_sequence] DESC)
            INCLUDE ([status], [occurred_at_utc]);

        CREATE INDEX [ix_signal_status_events_status]
            ON [intelligence].[signal_status_events]
            ([status], [occurred_at_utc] DESC);
    END;

    UPDATE [operations].[database_metadata]
    SET
        [schema_baseline_version] = 'V0004',
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
