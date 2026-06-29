/*
Migration: V0007__create_execution_and_reconciliation_tables.sql
Purpose:
  Create canonical broker-account references, versioned order-transition policies,
  immutable execution commands, append-only command/order/fill events, mutable current
  projections, redacted broker request evidence, unknown-outcome reconciliation and
  discrepancy resolution records.
Dependencies:
  V0001__create_schemas_and_migration_metadata.sql
  V0002__create_reference_tables.sql
  V0006__create_risk_and_trade_plan_tables.sql
Expected runtime impact:
  Additive DDL only. No broker calls, data scans or backfills are performed.
Locking considerations:
  Schema modification locks are acquired while tables, constraints and indexes are created.
Backward-compatibility window:
  Fully additive.
Data migration requirements:
  None.
Verification script:
  database/verification/V0007__verify_execution_and_reconciliation_tables.sql
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

    IF SCHEMA_ID(N'execution') IS NULL OR SCHEMA_ID(N'broker') IS NULL
        THROW 57001, 'V0001 is required: execution or broker schema does not exist.', 1;

    IF OBJECT_ID(N'[reference].[brokers]', N'U') IS NULL
        THROW 57002, 'V0002 is required: reference.brokers does not exist.', 1;

    IF OBJECT_ID(N'[reference].[instruments]', N'U') IS NULL
        THROW 57003, 'V0002 is required: reference.instruments does not exist.', 1;

    IF OBJECT_ID(N'[risk].[trade_plans]', N'U') IS NULL
        THROW 57004, 'V0006 is required: risk.trade_plans does not exist.', 1;

    IF OBJECT_ID(N'[broker].[broker_accounts]', N'U') IS NULL
    BEGIN
        CREATE TABLE [broker].[broker_accounts]
        (
            [broker_account_id] bigint IDENTITY(1,1) NOT NULL,
            [broker_account_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_broker_accounts_uid] DEFAULT NEWSEQUENTIALID(),
            [broker_id] bigint NOT NULL,
            [environment] varchar(20) NOT NULL,
            [account_reference] varchar(100) NOT NULL,
            [account_display_name] nvarchar(200) NOT NULL,
            [account_type] varchar(30) NOT NULL,
            [base_currency_code] char(3) NOT NULL,
            [status] varchar(20) NOT NULL,
            [allows_new_exposure] bit NOT NULL
                CONSTRAINT [df_broker_accounts_allows_new_exposure] DEFAULT (0),
            [allows_risk_reducing_exits] bit NOT NULL
                CONSTRAINT [df_broker_accounts_allows_exits] DEFAULT (1),
            [effective_from_utc] datetime2(7) NOT NULL,
            [effective_to_utc] datetime2(7) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_broker_accounts_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_broker_accounts_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_broker_accounts]
                PRIMARY KEY CLUSTERED ([broker_account_id]),
            CONSTRAINT [uq_broker_accounts_uid]
                UNIQUE ([broker_account_uid]),
            CONSTRAINT [uq_broker_accounts_reference]
                UNIQUE ([environment], [broker_id], [account_reference]),
            CONSTRAINT [fk_broker_accounts_broker]
                FOREIGN KEY ([broker_id])
                REFERENCES [reference].[brokers] ([broker_id]),
            CONSTRAINT [ck_broker_accounts_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_broker_accounts_type]
                CHECK ([account_type] IN ('PAPER', 'TRADING', 'CLEARING', 'CUSTODY')),
            CONSTRAINT [ck_broker_accounts_currency]
                CHECK (LEN(RTRIM([base_currency_code])) = 3),
            CONSTRAINT [ck_broker_accounts_status]
                CHECK ([status] IN ('ACTIVE', 'RESTRICTED', 'CLOSE_ONLY', 'SUSPENDED', 'CLOSED')),
            CONSTRAINT [ck_broker_accounts_exit_safety]
                CHECK ([allows_new_exposure] = 0 OR [allows_risk_reducing_exits] = 1),
            CONSTRAINT [ck_broker_accounts_effective_window]
                CHECK ([effective_to_utc] IS NULL OR [effective_to_utc] > [effective_from_utc])
        );

        CREATE INDEX [ix_broker_accounts_status]
            ON [broker].[broker_accounts]
            ([environment], [status], [broker_id], [account_reference]);
    END;

    IF OBJECT_ID(N'[execution].[order_transition_policies]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[order_transition_policies]
        (
            [order_transition_policy_id] bigint IDENTITY(1,1) NOT NULL,
            [order_transition_policy_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_order_transition_policies_uid] DEFAULT NEWSEQUENTIALID(),
            [policy_version] varchar(50) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [status] varchar(20) NOT NULL,
            [effective_from_utc] datetime2(7) NOT NULL,
            [effective_to_utc] datetime2(7) NULL,
            [checksum] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL,
            [created_by] nvarchar(200) NOT NULL,
            [approved_at_utc] datetime2(7) NULL,
            [approved_by] nvarchar(200) NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_record_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_order_transition_policies_created_record_at_utc]
                DEFAULT SYSUTCDATETIME(),
            [created_record_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_order_transition_policies]
                PRIMARY KEY CLUSTERED ([order_transition_policy_id]),
            CONSTRAINT [uq_order_transition_policies_uid]
                UNIQUE ([order_transition_policy_uid]),
            CONSTRAINT [uq_order_transition_policies_version]
                UNIQUE ([policy_version], [environment]),
            CONSTRAINT [ck_order_transition_policies_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_order_transition_policies_status]
                CHECK ([status] IN ('DRAFT', 'APPROVED', 'ACTIVE', 'SUSPENDED', 'RETIRED', 'REJECTED')),
            CONSTRAINT [ck_order_transition_policies_window]
                CHECK ([effective_to_utc] IS NULL OR [effective_to_utc] > [effective_from_utc]),
            CONSTRAINT [ck_order_transition_policies_approval]
                CHECK
                (
                    ([status] IN ('DRAFT', 'REJECTED')
                        AND [approved_at_utc] IS NULL AND [approved_by] IS NULL)
                    OR
                    ([status] NOT IN ('DRAFT', 'REJECTED')
                        AND [approved_at_utc] IS NOT NULL AND [approved_by] IS NOT NULL)
                ),
            CONSTRAINT [ck_order_transition_policies_checksum]
                CHECK
                (
                    LEN(RTRIM([checksum])) = 64
                    AND [checksum] NOT LIKE '%[^0-9A-Fa-f]%'
                ),
            CONSTRAINT [ck_order_transition_policies_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE INDEX [ix_order_transition_policies_effective]
            ON [execution].[order_transition_policies]
            ([environment], [status], [effective_from_utc] DESC)
            INCLUDE ([policy_version], [effective_to_utc]);
    END;

    IF OBJECT_ID(N'[execution].[order_transition_rules]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[order_transition_rules]
        (
            [order_transition_rule_id] bigint IDENTITY(1,1) NOT NULL,
            [order_transition_policy_id] bigint NOT NULL,
            [rule_sequence] int NOT NULL,
            [from_status] varchar(30) NULL,
            [event_type] varchar(30) NOT NULL,
            [to_status] varchar(30) NOT NULL,
            [command_type] varchar(20) NULL,
            [is_reconciliation_only] bit NOT NULL
                CONSTRAINT [df_order_transition_rules_reconciliation_only] DEFAULT (0),
            [is_terminal_target] bit NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_order_transition_rules_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_order_transition_rules]
                PRIMARY KEY CLUSTERED ([order_transition_rule_id]),
            CONSTRAINT [uq_order_transition_rules_sequence]
                UNIQUE ([order_transition_policy_id], [rule_sequence]),
            CONSTRAINT [uq_order_transition_rules_transition]
                UNIQUE
                ([order_transition_policy_id], [from_status], [event_type], [to_status], [command_type]),
            CONSTRAINT [fk_order_transition_rules_policy]
                FOREIGN KEY ([order_transition_policy_id])
                REFERENCES [execution].[order_transition_policies] ([order_transition_policy_id]),
            CONSTRAINT [ck_order_transition_rules_sequence]
                CHECK ([rule_sequence] >= 1),
            CONSTRAINT [ck_order_transition_rules_from_status]
                CHECK
                (
                    [from_status] IS NULL OR [from_status] IN
                    (
                        'CREATED', 'VALIDATED', 'SUBMISSION_PENDING', 'SUBMITTED',
                        'ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'MODIFY_PENDING',
                        'MODIFIED', 'CANCEL_PENDING', 'CANCELLED', 'REJECTED', 'FAILED',
                        'EXPIRED', 'RECONCILIATION_REQUIRED'
                    )
                ),
            CONSTRAINT [ck_order_transition_rules_event_type]
                CHECK
                (
                    [event_type] IN
                    (
                        'CREATED', 'VALIDATED', 'SUBMISSION_PENDING', 'SUBMITTED',
                        'ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'MODIFY_PENDING',
                        'MODIFIED', 'CANCEL_PENDING', 'CANCELLED', 'REJECTED', 'FAILED',
                        'EXPIRED', 'RECONCILIATION_REQUIRED', 'RECONCILED'
                    )
                ),
            CONSTRAINT [ck_order_transition_rules_to_status]
                CHECK
                (
                    [to_status] IN
                    (
                        'CREATED', 'VALIDATED', 'SUBMISSION_PENDING', 'SUBMITTED',
                        'ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'MODIFY_PENDING',
                        'MODIFIED', 'CANCEL_PENDING', 'CANCELLED', 'REJECTED', 'FAILED',
                        'EXPIRED', 'RECONCILIATION_REQUIRED'
                    )
                ),
            CONSTRAINT [ck_order_transition_rules_command_type]
                CHECK ([command_type] IS NULL OR [command_type] IN ('PLACE', 'MODIFY', 'CANCEL')),
            CONSTRAINT [ck_order_transition_rules_terminal]
                CHECK
                (
                    ([to_status] IN ('FILLED', 'CANCELLED', 'REJECTED', 'FAILED', 'EXPIRED')
                        AND [is_terminal_target] = 1)
                    OR
                    ([to_status] NOT IN ('FILLED', 'CANCELLED', 'REJECTED', 'FAILED', 'EXPIRED')
                        AND [is_terminal_target] = 0)
                )
        );
    END;

    IF OBJECT_ID(N'[execution].[execution_commands]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[execution_commands]
        (
            [execution_command_id] bigint IDENTITY(1,1) NOT NULL,
            [execution_command_uid] uniqueidentifier NOT NULL,
            [message_uid] uniqueidentifier NOT NULL,
            [trade_plan_id] bigint NOT NULL,
            [broker_account_id] bigint NOT NULL,
            [target_order_uid] uniqueidentifier NULL,
            [instrument_id] bigint NULL,
            [contract_version] varchar(20) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [command_type] varchar(20) NOT NULL,
            [idempotency_key] varchar(200) NOT NULL,
            [execution_policy_version] varchar(50) NOT NULL,
            [side] varchar(10) NULL,
            [position_intent] varchar(20) NULL,
            [quantity] decimal(19,6) NULL,
            [order_type] varchar(20) NULL,
            [limit_price] decimal(19,6) NULL,
            [trigger_price] decimal(19,6) NULL,
            [time_in_force] varchar(10) NULL,
            [client_order_id] varchar(100) NULL,
            [expected_order_version] int NULL,
            [change_reason_code] varchar(100) NULL,
            [requested_at_utc] datetime2(7) NULL,
            [generated_at_utc] datetime2(7) NOT NULL,
            [valid_until_utc] datetime2(7) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [raw_contract_json] nvarchar(max) NOT NULL,
            [contract_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_execution_commands_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_execution_commands]
                PRIMARY KEY CLUSTERED ([execution_command_id]),
            CONSTRAINT [uq_execution_commands_uid]
                UNIQUE ([execution_command_uid]),
            CONSTRAINT [uq_execution_commands_message_uid]
                UNIQUE ([message_uid]),
            CONSTRAINT [uq_execution_commands_id_instrument]
                UNIQUE ([execution_command_id], [instrument_id]),
            CONSTRAINT [uq_execution_commands_idempotency]
                UNIQUE ([environment], [broker_account_id], [idempotency_key]),
            CONSTRAINT [fk_execution_commands_trade_plan]
                FOREIGN KEY ([trade_plan_id])
                REFERENCES [risk].[trade_plans] ([trade_plan_id]),
            CONSTRAINT [fk_execution_commands_broker_account]
                FOREIGN KEY ([broker_account_id])
                REFERENCES [broker].[broker_accounts] ([broker_account_id]),
            CONSTRAINT [fk_execution_commands_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [ck_execution_commands_contract_version]
                CHECK ([contract_version] = '1.0.0'),
            CONSTRAINT [ck_execution_commands_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_execution_commands_type]
                CHECK ([command_type] IN ('PLACE', 'MODIFY', 'CANCEL')),
            CONSTRAINT [ck_execution_commands_side]
                CHECK ([side] IS NULL OR [side] IN ('BUY', 'SELL')),
            CONSTRAINT [ck_execution_commands_position_intent]
                CHECK
                (
                    [position_intent] IS NULL
                    OR [position_intent] IN ('INTRADAY', 'DELIVERY', 'CARRY_FORWARD')
                ),
            CONSTRAINT [ck_execution_commands_order_type]
                CHECK
                (
                    [order_type] IS NULL
                    OR [order_type] IN ('MARKET', 'LIMIT', 'STOP_MARKET', 'STOP_LIMIT')
                ),
            CONSTRAINT [ck_execution_commands_time_in_force]
                CHECK ([time_in_force] IS NULL OR [time_in_force] IN ('DAY', 'IOC')),
            CONSTRAINT [ck_execution_commands_values]
                CHECK
                (
                    ([quantity] IS NULL OR [quantity] > 0)
                    AND ([limit_price] IS NULL OR [limit_price] > 0)
                    AND ([trigger_price] IS NULL OR [trigger_price] > 0)
                    AND ([expected_order_version] IS NULL OR [expected_order_version] >= 0)
                ),
            CONSTRAINT [ck_execution_commands_place_fields]
                CHECK
                (
                    [command_type] <> 'PLACE'
                    OR
                    (
                        [target_order_uid] IS NULL
                        AND [expected_order_version] IS NULL
                        AND [change_reason_code] IS NULL
                        AND [instrument_id] IS NOT NULL
                        AND [side] IS NOT NULL
                        AND [position_intent] IS NOT NULL
                        AND [quantity] IS NOT NULL
                        AND [order_type] IS NOT NULL
                        AND [time_in_force] IS NOT NULL
                        AND [client_order_id] IS NOT NULL
                    )
                ),
            CONSTRAINT [ck_execution_commands_modify_fields]
                CHECK
                (
                    [command_type] <> 'MODIFY'
                    OR
                    (
                        [target_order_uid] IS NOT NULL
                        AND [expected_order_version] IS NOT NULL
                        AND [change_reason_code] IS NOT NULL
                        AND
                        (
                            [quantity] IS NOT NULL OR [order_type] IS NOT NULL
                            OR [limit_price] IS NOT NULL OR [trigger_price] IS NOT NULL
                        )
                    )
                ),
            CONSTRAINT [ck_execution_commands_cancel_fields]
                CHECK
                (
                    [command_type] <> 'CANCEL'
                    OR
                    (
                        [target_order_uid] IS NOT NULL
                        AND [expected_order_version] IS NOT NULL
                        AND [change_reason_code] IS NOT NULL
                        AND [quantity] IS NULL
                        AND [order_type] IS NULL
                        AND [limit_price] IS NULL
                        AND [trigger_price] IS NULL
                    )
                ),
            CONSTRAINT [ck_execution_commands_price_fields]
                CHECK
                (
                    [order_type] IS NULL
                    OR ([order_type] = 'MARKET' AND [limit_price] IS NULL AND [trigger_price] IS NULL)
                    OR ([order_type] = 'LIMIT' AND [limit_price] IS NOT NULL AND [trigger_price] IS NULL)
                    OR ([order_type] = 'STOP_MARKET' AND [limit_price] IS NULL AND [trigger_price] IS NOT NULL)
                    OR ([order_type] = 'STOP_LIMIT' AND [limit_price] IS NOT NULL AND [trigger_price] IS NOT NULL)
                ),
            CONSTRAINT [ck_execution_commands_validity]
                CHECK
                (
                    [valid_until_utc] > [generated_at_utc]
                    AND ([requested_at_utc] IS NULL OR [requested_at_utc] >= [generated_at_utc])
                ),
            CONSTRAINT [ck_execution_commands_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_execution_commands_raw_contract_json]
                CHECK (ISJSON([raw_contract_json]) = 1),
            CONSTRAINT [ck_execution_commands_contract_hash]
                CHECK
                (
                    LEN(RTRIM([contract_hash])) = 64
                    AND [contract_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE UNIQUE INDEX [ux_execution_commands_client_order]
            ON [execution].[execution_commands] ([broker_account_id], [client_order_id])
            WHERE [client_order_id] IS NOT NULL;

        CREATE INDEX [ix_execution_commands_trade_plan]
            ON [execution].[execution_commands]
            ([trade_plan_id], [generated_at_utc] DESC)
            INCLUDE ([command_type], [broker_account_id], [valid_until_utc]);

        CREATE INDEX [ix_execution_commands_target_order]
            ON [execution].[execution_commands]
            ([target_order_uid], [generated_at_utc] DESC)
            WHERE [target_order_uid] IS NOT NULL;

        CREATE INDEX [ix_execution_commands_correlation]
            ON [execution].[execution_commands] ([correlation_id], [generated_at_utc]);
    END;

    IF OBJECT_ID(N'[execution].[execution_command_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[execution_command_events]
        (
            [execution_command_event_id] bigint IDENTITY(1,1) NOT NULL,
            [execution_command_event_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_execution_command_events_uid] DEFAULT NEWSEQUENTIALID(),
            [execution_command_id] bigint NOT NULL,
            [event_sequence] int NOT NULL,
            [event_type] varchar(40) NOT NULL,
            [command_status] varchar(40) NOT NULL,
            [outcome_classification] varchar(50) NOT NULL,
            [reason_code] varchar(100) NULL,
            [reason_message] nvarchar(2000) NULL,
            [occurred_at_utc] datetime2(7) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_execution_command_events_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_execution_command_events]
                PRIMARY KEY CLUSTERED ([execution_command_event_id]),
            CONSTRAINT [uq_execution_command_events_uid]
                UNIQUE ([execution_command_event_uid]),
            CONSTRAINT [uq_execution_command_events_sequence]
                UNIQUE ([execution_command_id], [event_sequence]),
            CONSTRAINT [fk_execution_command_events_command]
                FOREIGN KEY ([execution_command_id])
                REFERENCES [execution].[execution_commands] ([execution_command_id]),
            CONSTRAINT [ck_execution_command_events_sequence]
                CHECK ([event_sequence] >= 0),
            CONSTRAINT [ck_execution_command_events_type]
                CHECK
                (
                    [event_type] IN
                    (
                        'RECEIVED', 'VALIDATED', 'REJECTED', 'PERSISTED', 'DISPATCH_PENDING',
                        'SUBMISSION_STARTED', 'SUBMISSION_SUCCEEDED', 'SUBMISSION_FAILED',
                        'UNKNOWN_OUTCOME', 'RECONCILIATION_STARTED', 'RECONCILED',
                        'COMPLETED', 'EXPIRED', 'CANCELLED'
                    )
                ),
            CONSTRAINT [ck_execution_command_events_status]
                CHECK
                (
                    [command_status] IN
                    (
                        'RECEIVED', 'VALIDATED', 'REJECTED', 'PERSISTED', 'DISPATCH_PENDING',
                        'SUBMISSION_PENDING', 'SUBMITTED', 'ACKNOWLEDGED', 'COMPLETED',
                        'FAILED', 'EXPIRED', 'RECONCILIATION_REQUIRED', 'CANCELLED'
                    )
                ),
            CONSTRAINT [ck_execution_command_events_outcome]
                CHECK
                (
                    [outcome_classification] IN
                    (
                        'NONE', 'PERMANENT_REJECTION', 'UNSUPPORTED_CAPABILITY',
                        'AUTHENTICATION_FAILURE', 'AUTHORIZATION_FAILURE', 'RATE_LIMITED',
                        'SESSION_RESTRICTION', 'INSTRUMENT_UNAVAILABLE', 'INSUFFICIENT_FUNDS',
                        'EXCHANGE_REJECTION', 'PRE_SUBMISSION_TRANSIENT',
                        'TRANSIENT_BROKER_FAILURE', 'UNKNOWN_POST_SUBMISSION',
                        'RECONCILIATION_CONFLICT', 'INTERNAL_INVARIANT_VIOLATION'
                    )
                ),
            CONSTRAINT [ck_execution_command_events_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE INDEX [ix_execution_command_events_latest]
            ON [execution].[execution_command_events]
            ([execution_command_id], [event_sequence] DESC)
            INCLUDE ([command_status], [outcome_classification], [occurred_at_utc]);
    END;

    IF OBJECT_ID(N'[execution].[execution_command_states]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[execution_command_states]
        (
            [execution_command_state_id] bigint IDENTITY(1,1) NOT NULL,
            [execution_command_id] bigint NOT NULL,
            [current_status] varchar(40) NOT NULL,
            [outcome_classification] varchar(50) NOT NULL,
            [last_event_sequence] int NOT NULL,
            [result_order_uid] uniqueidentifier NULL,
            [can_retry_without_reconciliation] bit NOT NULL
                CONSTRAINT [df_execution_command_states_retry] DEFAULT (0),
            [broker_contacted] bit NOT NULL
                CONSTRAINT [df_execution_command_states_broker_contacted] DEFAULT (0),
            [reconciliation_required] bit NOT NULL
                CONSTRAINT [df_execution_command_states_reconciliation] DEFAULT (0),
            [last_error_code] varchar(100) NULL,
            [last_error_message] nvarchar(2000) NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_execution_command_states_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_execution_command_states]
                PRIMARY KEY CLUSTERED ([execution_command_state_id]),
            CONSTRAINT [uq_execution_command_states_command]
                UNIQUE ([execution_command_id]),
            CONSTRAINT [fk_execution_command_states_command]
                FOREIGN KEY ([execution_command_id])
                REFERENCES [execution].[execution_commands] ([execution_command_id]),
            CONSTRAINT [ck_execution_command_states_status]
                CHECK
                (
                    [current_status] IN
                    (
                        'RECEIVED', 'VALIDATED', 'REJECTED', 'PERSISTED', 'DISPATCH_PENDING',
                        'SUBMISSION_PENDING', 'SUBMITTED', 'ACKNOWLEDGED', 'COMPLETED',
                        'FAILED', 'EXPIRED', 'RECONCILIATION_REQUIRED', 'CANCELLED'
                    )
                ),
            CONSTRAINT [ck_execution_command_states_outcome]
                CHECK
                (
                    [outcome_classification] IN
                    (
                        'NONE', 'PERMANENT_REJECTION', 'UNSUPPORTED_CAPABILITY',
                        'AUTHENTICATION_FAILURE', 'AUTHORIZATION_FAILURE', 'RATE_LIMITED',
                        'SESSION_RESTRICTION', 'INSTRUMENT_UNAVAILABLE', 'INSUFFICIENT_FUNDS',
                        'EXCHANGE_REJECTION', 'PRE_SUBMISSION_TRANSIENT',
                        'TRANSIENT_BROKER_FAILURE', 'UNKNOWN_POST_SUBMISSION',
                        'RECONCILIATION_CONFLICT', 'INTERNAL_INVARIANT_VIOLATION'
                    )
                ),
            CONSTRAINT [ck_execution_command_states_sequence]
                CHECK ([last_event_sequence] >= 0),
            CONSTRAINT [ck_execution_command_states_retry_safety]
                CHECK
                (
                    [can_retry_without_reconciliation] = 0
                    OR
                    (
                        [outcome_classification] = 'PRE_SUBMISSION_TRANSIENT'
                        AND [broker_contacted] = 0
                        AND [reconciliation_required] = 0
                    )
                ),
            CONSTRAINT [ck_execution_command_states_unknown_outcome]
                CHECK
                (
                    [outcome_classification] <> 'UNKNOWN_POST_SUBMISSION'
                    OR
                    (
                        [broker_contacted] = 1
                        AND [reconciliation_required] = 1
                        AND [can_retry_without_reconciliation] = 0
                    )
                )
        );
    END;

    IF OBJECT_ID(N'[execution].[orders]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[orders]
        (
            [order_id] bigint IDENTITY(1,1) NOT NULL,
            [order_uid] uniqueidentifier NOT NULL,
            [place_execution_command_id] bigint NOT NULL,
            [trade_plan_id] bigint NOT NULL,
            [broker_account_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [environment] varchar(20) NOT NULL,
            [side] varchar(10) NOT NULL,
            [position_intent] varchar(20) NOT NULL,
            [requested_quantity] decimal(19,6) NOT NULL,
            [filled_quantity] decimal(19,6) NOT NULL
                CONSTRAINT [df_orders_filled_quantity] DEFAULT (0),
            [remaining_quantity] decimal(19,6) NOT NULL,
            [average_fill_price] decimal(19,6) NULL,
            [order_type] varchar(20) NOT NULL,
            [limit_price] decimal(19,6) NULL,
            [trigger_price] decimal(19,6) NULL,
            [time_in_force] varchar(10) NOT NULL,
            [client_order_id] varchar(100) NOT NULL,
            [broker_order_id] varchar(200) NULL,
            [broker_client_tag] varchar(200) NULL,
            [current_status] varchar(30) NOT NULL,
            [current_order_version] int NOT NULL,
            [last_accepted_event_at_utc] datetime2(7) NOT NULL,
            [is_terminal] bit NOT NULL,
            [reconciliation_required] bit NOT NULL
                CONSTRAINT [df_orders_reconciliation_required] DEFAULT (0),
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_orders_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_orders_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_orders]
                PRIMARY KEY CLUSTERED ([order_id]),
            CONSTRAINT [uq_orders_uid]
                UNIQUE ([order_uid]),
            CONSTRAINT [uq_orders_id_instrument]
                UNIQUE ([order_id], [instrument_id]),
            CONSTRAINT [uq_orders_place_command]
                UNIQUE ([place_execution_command_id]),
            CONSTRAINT [uq_orders_client_order]
                UNIQUE ([broker_account_id], [client_order_id]),
            CONSTRAINT [fk_orders_place_command]
                FOREIGN KEY ([place_execution_command_id])
                REFERENCES [execution].[execution_commands] ([execution_command_id]),
            CONSTRAINT [fk_orders_trade_plan]
                FOREIGN KEY ([trade_plan_id], [instrument_id])
                REFERENCES [risk].[trade_plans] ([trade_plan_id], [instrument_id]),
            CONSTRAINT [fk_orders_broker_account]
                FOREIGN KEY ([broker_account_id])
                REFERENCES [broker].[broker_accounts] ([broker_account_id]),
            CONSTRAINT [fk_orders_instrument]
                FOREIGN KEY ([instrument_id])
                REFERENCES [reference].[instruments] ([instrument_id]),
            CONSTRAINT [ck_orders_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_orders_side]
                CHECK ([side] IN ('BUY', 'SELL')),
            CONSTRAINT [ck_orders_position_intent]
                CHECK ([position_intent] IN ('INTRADAY', 'DELIVERY', 'CARRY_FORWARD')),
            CONSTRAINT [ck_orders_quantities]
                CHECK
                (
                    [requested_quantity] > 0
                    AND [filled_quantity] >= 0
                    AND [remaining_quantity] >= 0
                    AND [filled_quantity] <= [requested_quantity]
                    AND [remaining_quantity] <= [requested_quantity]
                    AND [filled_quantity] + [remaining_quantity] = [requested_quantity]
                ),
            CONSTRAINT [ck_orders_average_fill]
                CHECK
                (
                    ([filled_quantity] = 0 AND [average_fill_price] IS NULL)
                    OR ([filled_quantity] > 0 AND [average_fill_price] > 0)
                ),
            CONSTRAINT [ck_orders_order_type]
                CHECK ([order_type] IN ('MARKET', 'LIMIT', 'STOP_MARKET', 'STOP_LIMIT')),
            CONSTRAINT [ck_orders_price_fields]
                CHECK
                (
                    ([order_type] = 'MARKET' AND [limit_price] IS NULL AND [trigger_price] IS NULL)
                    OR ([order_type] = 'LIMIT' AND [limit_price] > 0 AND [trigger_price] IS NULL)
                    OR ([order_type] = 'STOP_MARKET' AND [limit_price] IS NULL AND [trigger_price] > 0)
                    OR ([order_type] = 'STOP_LIMIT' AND [limit_price] > 0 AND [trigger_price] > 0)
                ),
            CONSTRAINT [ck_orders_time_in_force]
                CHECK ([time_in_force] IN ('DAY', 'IOC')),
            CONSTRAINT [ck_orders_status]
                CHECK
                (
                    [current_status] IN
                    (
                        'CREATED', 'VALIDATED', 'SUBMISSION_PENDING', 'SUBMITTED',
                        'ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'MODIFY_PENDING',
                        'MODIFIED', 'CANCEL_PENDING', 'CANCELLED', 'REJECTED', 'FAILED',
                        'EXPIRED', 'RECONCILIATION_REQUIRED'
                    )
                ),
            CONSTRAINT [ck_orders_version]
                CHECK ([current_order_version] >= 0),
            CONSTRAINT [ck_orders_terminal]
                CHECK
                (
                    ([current_status] IN ('FILLED', 'CANCELLED', 'REJECTED', 'FAILED', 'EXPIRED')
                        AND [is_terminal] = 1)
                    OR
                    ([current_status] NOT IN ('FILLED', 'CANCELLED', 'REJECTED', 'FAILED', 'EXPIRED')
                        AND [is_terminal] = 0)
                ),
            CONSTRAINT [ck_orders_reconciliation_status]
                CHECK
                (
                    [reconciliation_required] = 0
                    OR [current_status] = 'RECONCILIATION_REQUIRED'
                )
        );

        CREATE UNIQUE INDEX [ux_orders_broker_order]
            ON [execution].[orders] ([broker_account_id], [broker_order_id])
            WHERE [broker_order_id] IS NOT NULL;

        CREATE INDEX [ix_orders_status]
            ON [execution].[orders]
            ([broker_account_id], [current_status], [updated_at_utc] DESC)
            INCLUDE
            (
                [instrument_id], [side], [requested_quantity], [filled_quantity],
                [remaining_quantity], [current_order_version], [reconciliation_required]
            );

        CREATE INDEX [ix_orders_trade_plan]
            ON [execution].[orders] ([trade_plan_id], [created_at_utc] DESC);
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.foreign_keys
        WHERE [parent_object_id] = OBJECT_ID(N'[execution].[execution_commands]')
          AND [name] = N'fk_execution_commands_target_order'
    )
    BEGIN
        ALTER TABLE [execution].[execution_commands]
        ADD CONSTRAINT [fk_execution_commands_target_order]
            FOREIGN KEY ([target_order_uid])
            REFERENCES [execution].[orders] ([order_uid]);
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.foreign_keys
        WHERE [parent_object_id] = OBJECT_ID(N'[execution].[execution_command_states]')
          AND [name] = N'fk_execution_command_states_result_order'
    )
    BEGIN
        ALTER TABLE [execution].[execution_command_states]
        ADD CONSTRAINT [fk_execution_command_states_result_order]
            FOREIGN KEY ([result_order_uid])
            REFERENCES [execution].[orders] ([order_uid]);
    END;

    IF OBJECT_ID(N'[execution].[order_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[order_events]
        (
            [order_event_id] bigint IDENTITY(1,1) NOT NULL,
            [order_event_uid] uniqueidentifier NOT NULL,
            [message_uid] uniqueidentifier NOT NULL,
            [order_id] bigint NOT NULL,
            [execution_command_id] bigint NOT NULL,
            [trade_plan_id] bigint NOT NULL,
            [broker_account_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [contract_version] varchar(20) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [side] varchar(10) NOT NULL,
            [broker_order_id] varchar(200) NULL,
            [event_type] varchar(30) NOT NULL,
            [previous_status] varchar(30) NULL,
            [normalized_status] varchar(30) NOT NULL,
            [broker_status] varchar(100) NULL,
            [reason_code] varchar(100) NULL,
            [reason_message] nvarchar(2000) NULL,
            [requested_quantity] decimal(19,6) NULL,
            [filled_quantity] decimal(19,6) NULL,
            [remaining_quantity] decimal(19,6) NULL,
            [average_fill_price] decimal(19,6) NULL,
            [limit_price] decimal(19,6) NULL,
            [trigger_price] decimal(19,6) NULL,
            [event_at_utc] datetime2(7) NOT NULL,
            [received_at_utc] datetime2(7) NOT NULL,
            [generated_at_utc] datetime2(7) NOT NULL,
            [order_version] int NOT NULL,
            [broker_sequence] varchar(200) NULL,
            [is_reconciliation_event] bit NOT NULL
                CONSTRAINT [df_order_events_reconciliation_event] DEFAULT (0),
            [projection_disposition] varchar(30) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [raw_contract_json] nvarchar(max) NOT NULL,
            [contract_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_order_events_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_order_events]
                PRIMARY KEY CLUSTERED ([order_event_id]),
            CONSTRAINT [uq_order_events_uid]
                UNIQUE ([order_event_uid]),
            CONSTRAINT [uq_order_events_message_uid]
                UNIQUE ([message_uid]),
            CONSTRAINT [uq_order_events_order_version]
                UNIQUE ([order_id], [order_version]),
            CONSTRAINT [fk_order_events_order]
                FOREIGN KEY ([order_id], [instrument_id])
                REFERENCES [execution].[orders] ([order_id], [instrument_id]),
            CONSTRAINT [fk_order_events_command]
                FOREIGN KEY ([execution_command_id])
                REFERENCES [execution].[execution_commands] ([execution_command_id]),
            CONSTRAINT [fk_order_events_trade_plan]
                FOREIGN KEY ([trade_plan_id], [instrument_id])
                REFERENCES [risk].[trade_plans] ([trade_plan_id], [instrument_id]),
            CONSTRAINT [fk_order_events_broker_account]
                FOREIGN KEY ([broker_account_id])
                REFERENCES [broker].[broker_accounts] ([broker_account_id]),
            CONSTRAINT [ck_order_events_contract_version]
                CHECK ([contract_version] = '1.0.0'),
            CONSTRAINT [ck_order_events_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_order_events_side]
                CHECK ([side] IN ('BUY', 'SELL')),
            CONSTRAINT [ck_order_events_event_type]
                CHECK
                (
                    [event_type] IN
                    (
                        'CREATED', 'VALIDATED', 'SUBMISSION_PENDING', 'SUBMITTED',
                        'ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'MODIFY_PENDING',
                        'MODIFIED', 'CANCEL_PENDING', 'CANCELLED', 'REJECTED', 'FAILED',
                        'EXPIRED', 'RECONCILIATION_REQUIRED', 'RECONCILED'
                    )
                ),
            CONSTRAINT [ck_order_events_previous_status]
                CHECK
                (
                    [previous_status] IS NULL OR [previous_status] IN
                    (
                        'CREATED', 'VALIDATED', 'SUBMISSION_PENDING', 'SUBMITTED',
                        'ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'MODIFY_PENDING',
                        'MODIFIED', 'CANCEL_PENDING', 'CANCELLED', 'REJECTED', 'FAILED',
                        'EXPIRED', 'RECONCILIATION_REQUIRED'
                    )
                ),
            CONSTRAINT [ck_order_events_normalized_status]
                CHECK
                (
                    [normalized_status] IN
                    (
                        'CREATED', 'VALIDATED', 'SUBMISSION_PENDING', 'SUBMITTED',
                        'ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'MODIFY_PENDING',
                        'MODIFIED', 'CANCEL_PENDING', 'CANCELLED', 'REJECTED', 'FAILED',
                        'EXPIRED', 'RECONCILIATION_REQUIRED'
                    )
                ),
            CONSTRAINT [ck_order_events_quantities]
                CHECK
                (
                    ([requested_quantity] IS NULL OR [requested_quantity] >= 0)
                    AND ([filled_quantity] IS NULL OR [filled_quantity] >= 0)
                    AND ([remaining_quantity] IS NULL OR [remaining_quantity] >= 0)
                    AND ([requested_quantity] IS NULL OR [filled_quantity] IS NULL
                        OR [filled_quantity] <= [requested_quantity])
                    AND ([requested_quantity] IS NULL OR [remaining_quantity] IS NULL
                        OR [remaining_quantity] <= [requested_quantity])
                ),
            CONSTRAINT [ck_order_events_prices]
                CHECK
                (
                    ([average_fill_price] IS NULL OR [average_fill_price] > 0)
                    AND ([limit_price] IS NULL OR [limit_price] > 0)
                    AND ([trigger_price] IS NULL OR [trigger_price] > 0)
                ),
            CONSTRAINT [ck_order_events_time]
                CHECK
                (
                    [received_at_utc] >= [event_at_utc]
                    AND [generated_at_utc] >= [received_at_utc]
                ),
            CONSTRAINT [ck_order_events_version]
                CHECK ([order_version] >= 0),
            CONSTRAINT [ck_order_events_projection_disposition]
                CHECK ([projection_disposition] IN ('ACCEPTED', 'IGNORED_LATE', 'QUARANTINED')),
            CONSTRAINT [ck_order_events_reconciled_type]
                CHECK ([event_type] <> 'RECONCILED' OR [is_reconciliation_event] = 1),
            CONSTRAINT [ck_order_events_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_order_events_raw_contract_json]
                CHECK (ISJSON([raw_contract_json]) = 1),
            CONSTRAINT [ck_order_events_contract_hash]
                CHECK
                (
                    LEN(RTRIM([contract_hash])) = 64
                    AND [contract_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE UNIQUE INDEX [ux_order_events_broker_sequence]
            ON [execution].[order_events]
            ([broker_account_id], [broker_order_id], [broker_sequence])
            WHERE [broker_order_id] IS NOT NULL AND [broker_sequence] IS NOT NULL;

        CREATE INDEX [ix_order_events_order_time]
            ON [execution].[order_events]
            ([order_id], [event_at_utc], [received_at_utc], [order_event_id])
            INCLUDE
            (
                [event_type], [normalized_status], [order_version],
                [projection_disposition], [is_reconciliation_event]
            );

        CREATE INDEX [ix_order_events_projection]
            ON [execution].[order_events]
            ([projection_disposition], [received_at_utc] DESC)
            INCLUDE ([order_id], [normalized_status], [order_version]);
    END;

    IF OBJECT_ID(N'[execution].[order_event_quarantines]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[order_event_quarantines]
        (
            [order_event_quarantine_id] bigint IDENTITY(1,1) NOT NULL,
            [order_event_id] bigint NOT NULL,
            [transition_policy_id] bigint NULL,
            [quarantine_reason_code] varchar(100) NOT NULL,
            [quarantine_reason_message] nvarchar(2000) NOT NULL,
            [detected_at_utc] datetime2(7) NOT NULL,
            [resolution_status] varchar(20) NOT NULL,
            [resolved_at_utc] datetime2(7) NULL,
            [resolved_by] nvarchar(256) NULL,
            [resolution_notes] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_order_event_quarantines_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_order_event_quarantines]
                PRIMARY KEY CLUSTERED ([order_event_quarantine_id]),
            CONSTRAINT [uq_order_event_quarantines_event]
                UNIQUE ([order_event_id]),
            CONSTRAINT [fk_order_event_quarantines_event]
                FOREIGN KEY ([order_event_id])
                REFERENCES [execution].[order_events] ([order_event_id]),
            CONSTRAINT [fk_order_event_quarantines_policy]
                FOREIGN KEY ([transition_policy_id])
                REFERENCES [execution].[order_transition_policies] ([order_transition_policy_id]),
            CONSTRAINT [ck_order_event_quarantines_status]
                CHECK ([resolution_status] IN ('OPEN', 'ACCEPTED', 'REJECTED', 'RECONCILED')),
            CONSTRAINT [ck_order_event_quarantines_resolution]
                CHECK
                (
                    ([resolution_status] = 'OPEN'
                        AND [resolved_at_utc] IS NULL AND [resolved_by] IS NULL)
                    OR
                    ([resolution_status] <> 'OPEN'
                        AND [resolved_at_utc] IS NOT NULL AND [resolved_by] IS NOT NULL)
                )
        );

        CREATE INDEX [ix_order_event_quarantines_open]
            ON [execution].[order_event_quarantines]
            ([resolution_status], [detected_at_utc] DESC);
    END;

    IF OBJECT_ID(N'[execution].[fills]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[fills]
        (
            [fill_id] bigint IDENTITY(1,1) NOT NULL,
            [fill_uid] uniqueidentifier NOT NULL,
            [message_uid] uniqueidentifier NOT NULL,
            [order_id] bigint NOT NULL,
            [execution_command_id] bigint NOT NULL,
            [trade_plan_id] bigint NOT NULL,
            [broker_account_id] bigint NOT NULL,
            [instrument_id] bigint NOT NULL,
            [contract_version] varchar(20) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [broker_order_id] varchar(200) NULL,
            [broker_fill_id] varchar(200) NULL,
            [fill_fingerprint] varchar(256) NULL,
            [side] varchar(10) NOT NULL,
            [fill_quantity] decimal(19,6) NOT NULL,
            [fill_price] decimal(19,6) NOT NULL,
            [gross_amount] decimal(19,6) NULL,
            [fees_amount] decimal(19,6) NULL,
            [taxes_amount] decimal(19,6) NULL,
            [net_amount] decimal(19,6) NULL,
            [currency_code] char(3) NOT NULL,
            [liquidity_role] varchar(20) NULL,
            [fill_at_utc] datetime2(7) NOT NULL,
            [received_at_utc] datetime2(7) NOT NULL,
            [generated_at_utc] datetime2(7) NOT NULL,
            [broker_sequence] varchar(200) NULL,
            [settlement_date] date NULL,
            [is_reconciliation_fill] bit NOT NULL
                CONSTRAINT [df_fills_reconciliation_fill] DEFAULT (0),
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [raw_contract_json] nvarchar(max) NOT NULL,
            [contract_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_fills_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_fills]
                PRIMARY KEY CLUSTERED ([fill_id]),
            CONSTRAINT [uq_fills_uid]
                UNIQUE ([fill_uid]),
            CONSTRAINT [uq_fills_message_uid]
                UNIQUE ([message_uid]),
            CONSTRAINT [fk_fills_order]
                FOREIGN KEY ([order_id], [instrument_id])
                REFERENCES [execution].[orders] ([order_id], [instrument_id]),
            CONSTRAINT [fk_fills_command]
                FOREIGN KEY ([execution_command_id])
                REFERENCES [execution].[execution_commands] ([execution_command_id]),
            CONSTRAINT [fk_fills_trade_plan]
                FOREIGN KEY ([trade_plan_id], [instrument_id])
                REFERENCES [risk].[trade_plans] ([trade_plan_id], [instrument_id]),
            CONSTRAINT [fk_fills_broker_account]
                FOREIGN KEY ([broker_account_id])
                REFERENCES [broker].[broker_accounts] ([broker_account_id]),
            CONSTRAINT [ck_fills_contract_version]
                CHECK ([contract_version] = '1.0.0'),
            CONSTRAINT [ck_fills_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_fills_identity]
                CHECK ([broker_fill_id] IS NOT NULL OR [fill_fingerprint] IS NOT NULL),
            CONSTRAINT [ck_fills_side]
                CHECK ([side] IN ('BUY', 'SELL')),
            CONSTRAINT [ck_fills_values]
                CHECK
                (
                    [fill_quantity] > 0
                    AND [fill_price] > 0
                    AND ([gross_amount] IS NULL OR [gross_amount] >= 0)
                    AND ([fees_amount] IS NULL OR [fees_amount] >= 0)
                    AND ([taxes_amount] IS NULL OR [taxes_amount] >= 0)
                ),
            CONSTRAINT [ck_fills_currency]
                CHECK (LEN(RTRIM([currency_code])) = 3),
            CONSTRAINT [ck_fills_liquidity_role]
                CHECK ([liquidity_role] IS NULL OR [liquidity_role] IN ('MAKER', 'TAKER', 'UNKNOWN')),
            CONSTRAINT [ck_fills_time]
                CHECK
                (
                    [received_at_utc] >= [fill_at_utc]
                    AND [generated_at_utc] >= [received_at_utc]
                ),
            CONSTRAINT [ck_fills_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_fills_raw_contract_json]
                CHECK (ISJSON([raw_contract_json]) = 1),
            CONSTRAINT [ck_fills_contract_hash]
                CHECK
                (
                    LEN(RTRIM([contract_hash])) = 64
                    AND [contract_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE UNIQUE INDEX [ux_fills_broker_fill]
            ON [execution].[fills] ([broker_account_id], [broker_fill_id])
            WHERE [broker_fill_id] IS NOT NULL;

        CREATE UNIQUE INDEX [ux_fills_fingerprint]
            ON [execution].[fills] ([broker_account_id], [fill_fingerprint])
            WHERE [fill_fingerprint] IS NOT NULL;

        CREATE INDEX [ix_fills_order_time]
            ON [execution].[fills]
            ([order_id], [fill_at_utc], [fill_id])
            INCLUDE ([fill_quantity], [fill_price], [fees_amount], [taxes_amount]);
    END;

    IF OBJECT_ID(N'[broker].[broker_requests]', N'U') IS NULL
    BEGIN
        CREATE TABLE [broker].[broker_requests]
        (
            [broker_request_id] bigint IDENTITY(1,1) NOT NULL,
            [broker_request_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_broker_requests_uid] DEFAULT NEWSEQUENTIALID(),
            [execution_command_id] bigint NOT NULL,
            [broker_account_id] bigint NOT NULL,
            [attempt_number] int NOT NULL,
            [operation] varchar(30) NOT NULL,
            [endpoint_name] varchar(200) NOT NULL,
            [adapter_version] varchar(50) NOT NULL,
            [capability_version] varchar(50) NOT NULL,
            [request_started_at_utc] datetime2(7) NOT NULL,
            [request_completed_at_utc] datetime2(7) NULL,
            [transport_outcome] varchar(30) NOT NULL,
            [normalized_result] varchar(40) NOT NULL,
            [http_status_code] int NULL,
            [broker_error_code] varchar(200) NULL,
            [normalized_error_category] varchar(50) NULL,
            [broker_order_id] varchar(200) NULL,
            [broker_client_tag] varchar(200) NULL,
            [request_payload_hash] char(64) NOT NULL,
            [response_payload_hash] char(64) NULL,
            [redacted_request_json] nvarchar(max) NOT NULL,
            [redacted_response_json] nvarchar(max) NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_broker_requests_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_broker_requests]
                PRIMARY KEY CLUSTERED ([broker_request_id]),
            CONSTRAINT [uq_broker_requests_uid]
                UNIQUE ([broker_request_uid]),
            CONSTRAINT [uq_broker_requests_attempt]
                UNIQUE ([execution_command_id], [attempt_number]),
            CONSTRAINT [fk_broker_requests_command]
                FOREIGN KEY ([execution_command_id])
                REFERENCES [execution].[execution_commands] ([execution_command_id]),
            CONSTRAINT [fk_broker_requests_account]
                FOREIGN KEY ([broker_account_id])
                REFERENCES [broker].[broker_accounts] ([broker_account_id]),
            CONSTRAINT [ck_broker_requests_attempt]
                CHECK ([attempt_number] >= 1),
            CONSTRAINT [ck_broker_requests_operation]
                CHECK
                (
                    [operation] IN
                    ('PLACE_ORDER', 'MODIFY_ORDER', 'CANCEL_ORDER', 'GET_ORDER',
                     'GET_ORDER_HISTORY', 'GET_ORDER_TRADES', 'GET_ORDER_BOOK',
                     'GET_POSITIONS', 'GET_HOLDINGS', 'GET_FUNDS')
                ),
            CONSTRAINT [ck_broker_requests_transport_outcome]
                CHECK ([transport_outcome] IN ('NOT_SENT', 'RESPONSE_RECEIVED', 'TIMEOUT', 'CONNECTION_FAILURE')),
            CONSTRAINT [ck_broker_requests_normalized_result]
                CHECK
                (
                    [normalized_result] IN
                    ('PENDING', 'ACCEPTED', 'REJECTED', 'FAILED_PRE_SUBMISSION',
                     'UNKNOWN_OUTCOME', 'RECONCILIATION_EVIDENCE')
                ),
            CONSTRAINT [ck_broker_requests_http_status]
                CHECK ([http_status_code] IS NULL OR [http_status_code] BETWEEN 100 AND 599),
            CONSTRAINT [ck_broker_requests_error_category]
                CHECK
                (
                    [normalized_error_category] IS NULL OR [normalized_error_category] IN
                    (
                        'VALIDATION_FAILURE', 'UNSUPPORTED_CAPABILITY', 'AUTHENTICATION_FAILURE',
                        'AUTHORIZATION_RESTRICTION', 'RATE_LIMITED', 'SESSION_RESTRICTION',
                        'INSTRUMENT_UNAVAILABLE', 'INSUFFICIENT_FUNDS', 'EXCHANGE_REJECTION',
                        'TRANSIENT_BROKER_FAILURE', 'TRANSPORT_TIMEOUT', 'UNKNOWN_OUTCOME',
                        'RECONCILIATION_CONFLICT'
                    )
                ),
            CONSTRAINT [ck_broker_requests_completion]
                CHECK
                (
                    ([normalized_result] = 'PENDING' AND [request_completed_at_utc] IS NULL)
                    OR
                    ([normalized_result] <> 'PENDING'
                        AND [request_completed_at_utc] IS NOT NULL
                        AND [request_completed_at_utc] >= [request_started_at_utc])
                ),
            CONSTRAINT [ck_broker_requests_unknown_outcome]
                CHECK
                (
                    [normalized_result] <> 'UNKNOWN_OUTCOME'
                    OR [transport_outcome] IN ('TIMEOUT', 'CONNECTION_FAILURE')
                ),
            CONSTRAINT [ck_broker_requests_request_hash]
                CHECK
                (
                    LEN(RTRIM([request_payload_hash])) = 64
                    AND [request_payload_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                ),
            CONSTRAINT [ck_broker_requests_response_hash]
                CHECK
                (
                    [response_payload_hash] IS NULL
                    OR
                    (
                        LEN(RTRIM([response_payload_hash])) = 64
                        AND [response_payload_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                    )
                ),
            CONSTRAINT [ck_broker_requests_request_json]
                CHECK (ISJSON([redacted_request_json]) = 1),
            CONSTRAINT [ck_broker_requests_response_json]
                CHECK ([redacted_response_json] IS NULL OR ISJSON([redacted_response_json]) = 1)
        );

        CREATE INDEX [ix_broker_requests_command]
            ON [broker].[broker_requests]
            ([execution_command_id], [attempt_number] DESC)
            INCLUDE ([transport_outcome], [normalized_result], [broker_order_id]);

        CREATE INDEX [ix_broker_requests_unknown]
            ON [broker].[broker_requests]
            ([normalized_result], [request_started_at_utc] DESC)
            INCLUDE ([execution_command_id], [broker_account_id], [operation]);
    END;

    IF OBJECT_ID(N'[execution].[reconciliation_runs]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[reconciliation_runs]
        (
            [reconciliation_run_id] bigint IDENTITY(1,1) NOT NULL,
            [reconciliation_run_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_reconciliation_runs_uid] DEFAULT NEWSEQUENTIALID(),
            [broker_account_id] bigint NOT NULL,
            [environment] varchar(20) NOT NULL,
            [trigger_type] varchar(40) NOT NULL,
            [scope_type] varchar(30) NOT NULL,
            [scope_reference] varchar(200) NOT NULL,
            [status] varchar(20) NOT NULL,
            [started_at_utc] datetime2(7) NOT NULL,
            [completed_at_utc] datetime2(7) NULL,
            [observation_count] int NOT NULL
                CONSTRAINT [df_reconciliation_runs_observation_count] DEFAULT (0),
            [discrepancy_count] int NOT NULL
                CONSTRAINT [df_reconciliation_runs_discrepancy_count] DEFAULT (0),
            [unresolved_material_count] int NOT NULL
                CONSTRAINT [df_reconciliation_runs_unresolved_count] DEFAULT (0),
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [error_code] varchar(100) NULL,
            [error_message] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_reconciliation_runs_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_reconciliation_runs_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_reconciliation_runs]
                PRIMARY KEY CLUSTERED ([reconciliation_run_id]),
            CONSTRAINT [uq_reconciliation_runs_uid]
                UNIQUE ([reconciliation_run_uid]),
            CONSTRAINT [fk_reconciliation_runs_account]
                FOREIGN KEY ([broker_account_id])
                REFERENCES [broker].[broker_accounts] ([broker_account_id]),
            CONSTRAINT [ck_reconciliation_runs_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_reconciliation_runs_trigger]
                CHECK
                (
                    [trigger_type] IN
                    ('UNKNOWN_OUTCOME', 'STARTUP', 'PERIODIC', 'STREAM_RECOVERY',
                     'QUANTITY_MISMATCH', 'SESSION_SHUTDOWN', 'OPERATOR_REQUEST')
                ),
            CONSTRAINT [ck_reconciliation_runs_scope]
                CHECK ([scope_type] IN ('ACCOUNT', 'COMMAND', 'ORDER', 'SESSION')),
            CONSTRAINT [ck_reconciliation_runs_status]
                CHECK ([status] IN ('STARTED', 'SUCCEEDED', 'PARTIAL', 'FAILED', 'CANCELLED')),
            CONSTRAINT [ck_reconciliation_runs_counts]
                CHECK
                (
                    [observation_count] >= 0
                    AND [discrepancy_count] >= 0
                    AND [unresolved_material_count] >= 0
                    AND [unresolved_material_count] <= [discrepancy_count]
                ),
            CONSTRAINT [ck_reconciliation_runs_completion]
                CHECK
                (
                    ([status] = 'STARTED' AND [completed_at_utc] IS NULL)
                    OR
                    ([status] <> 'STARTED'
                        AND [completed_at_utc] IS NOT NULL
                        AND [completed_at_utc] >= [started_at_utc])
                )
        );

        CREATE INDEX [ix_reconciliation_runs_account]
            ON [execution].[reconciliation_runs]
            ([broker_account_id], [started_at_utc] DESC)
            INCLUDE ([trigger_type], [scope_type], [status], [unresolved_material_count]);
    END;

    IF OBJECT_ID(N'[execution].[reconciliation_observations]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[reconciliation_observations]
        (
            [reconciliation_observation_id] bigint IDENTITY(1,1) NOT NULL,
            [reconciliation_observation_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_reconciliation_observations_uid] DEFAULT NEWSEQUENTIALID(),
            [reconciliation_run_id] bigint NOT NULL,
            [broker_request_id] bigint NULL,
            [order_id] bigint NULL,
            [execution_command_id] bigint NULL,
            [observation_type] varchar(40) NOT NULL,
            [broker_order_id] varchar(200) NULL,
            [broker_fill_id] varchar(200) NULL,
            [client_tag] varchar(200) NULL,
            [observed_status] varchar(100) NULL,
            [observed_quantity] decimal(19,6) NULL,
            [observed_filled_quantity] decimal(19,6) NULL,
            [observed_average_price] decimal(19,6) NULL,
            [observed_at_utc] datetime2(7) NOT NULL,
            [received_at_utc] datetime2(7) NOT NULL,
            [payload_hash] char(64) NOT NULL,
            [redacted_payload_json] nvarchar(max) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_reconciliation_observations_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_reconciliation_observations]
                PRIMARY KEY CLUSTERED ([reconciliation_observation_id]),
            CONSTRAINT [uq_reconciliation_observations_uid]
                UNIQUE ([reconciliation_observation_uid]),
            CONSTRAINT [fk_reconciliation_observations_run]
                FOREIGN KEY ([reconciliation_run_id])
                REFERENCES [execution].[reconciliation_runs] ([reconciliation_run_id]),
            CONSTRAINT [fk_reconciliation_observations_request]
                FOREIGN KEY ([broker_request_id])
                REFERENCES [broker].[broker_requests] ([broker_request_id]),
            CONSTRAINT [fk_reconciliation_observations_order]
                FOREIGN KEY ([order_id])
                REFERENCES [execution].[orders] ([order_id]),
            CONSTRAINT [fk_reconciliation_observations_command]
                FOREIGN KEY ([execution_command_id])
                REFERENCES [execution].[execution_commands] ([execution_command_id]),
            CONSTRAINT [ck_reconciliation_observations_type]
                CHECK
                (
                    [observation_type] IN
                    ('ORDER', 'ORDER_HISTORY', 'ORDER_BOOK', 'TRADE', 'POSITION',
                     'HOLDING', 'FUNDS', 'CLIENT_TAG_LOOKUP')
                ),
            CONSTRAINT [ck_reconciliation_observations_values]
                CHECK
                (
                    ([observed_quantity] IS NULL OR [observed_quantity] >= 0)
                    AND ([observed_filled_quantity] IS NULL OR [observed_filled_quantity] >= 0)
                    AND ([observed_average_price] IS NULL OR [observed_average_price] > 0)
                ),
            CONSTRAINT [ck_reconciliation_observations_time]
                CHECK ([received_at_utc] >= [observed_at_utc]),
            CONSTRAINT [ck_reconciliation_observations_hash]
                CHECK
                (
                    LEN(RTRIM([payload_hash])) = 64
                    AND [payload_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                ),
            CONSTRAINT [ck_reconciliation_observations_json]
                CHECK (ISJSON([redacted_payload_json]) = 1)
        );

        CREATE INDEX [ix_reconciliation_observations_run]
            ON [execution].[reconciliation_observations]
            ([reconciliation_run_id], [observation_type], [observed_at_utc]);
    END;

    IF OBJECT_ID(N'[execution].[reconciliation_discrepancies]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[reconciliation_discrepancies]
        (
            [reconciliation_discrepancy_id] bigint IDENTITY(1,1) NOT NULL,
            [reconciliation_discrepancy_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_reconciliation_discrepancies_uid] DEFAULT NEWSEQUENTIALID(),
            [reconciliation_run_id] bigint NOT NULL,
            [order_id] bigint NULL,
            [execution_command_id] bigint NULL,
            [discrepancy_type] varchar(50) NOT NULL,
            [severity] varchar(20) NOT NULL,
            [status] varchar(20) NOT NULL,
            [description] nvarchar(2000) NOT NULL,
            [local_value_json] nvarchar(max) NULL,
            [broker_value_json] nvarchar(max) NULL,
            [detected_at_utc] datetime2(7) NOT NULL,
            [blocks_new_exposure] bit NOT NULL,
            [allows_risk_reducing_exits] bit NOT NULL,
            [resolved_at_utc] datetime2(7) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_reconciliation_discrepancies_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_reconciliation_discrepancies_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_reconciliation_discrepancies]
                PRIMARY KEY CLUSTERED ([reconciliation_discrepancy_id]),
            CONSTRAINT [uq_reconciliation_discrepancies_uid]
                UNIQUE ([reconciliation_discrepancy_uid]),
            CONSTRAINT [fk_reconciliation_discrepancies_run]
                FOREIGN KEY ([reconciliation_run_id])
                REFERENCES [execution].[reconciliation_runs] ([reconciliation_run_id]),
            CONSTRAINT [fk_reconciliation_discrepancies_order]
                FOREIGN KEY ([order_id])
                REFERENCES [execution].[orders] ([order_id]),
            CONSTRAINT [fk_reconciliation_discrepancies_command]
                FOREIGN KEY ([execution_command_id])
                REFERENCES [execution].[execution_commands] ([execution_command_id]),
            CONSTRAINT [ck_reconciliation_discrepancies_type]
                CHECK
                (
                    [discrepancy_type] IN
                    ('MISSING_LOCAL_ORDER', 'MISSING_BROKER_ORDER', 'STATUS_MISMATCH',
                     'QUANTITY_MISMATCH', 'FILL_MISMATCH', 'POSITION_MISMATCH',
                     'FUNDS_MISMATCH', 'DUPLICATE_BROKER_ORDER', 'UNKNOWN')
                ),
            CONSTRAINT [ck_reconciliation_discrepancies_severity]
                CHECK ([severity] IN ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')),
            CONSTRAINT [ck_reconciliation_discrepancies_status]
                CHECK ([status] IN ('OPEN', 'CONFIRMED', 'RESOLVED', 'IGNORED')),
            CONSTRAINT [ck_reconciliation_discrepancies_exit_safety]
                CHECK ([blocks_new_exposure] = 0 OR [allows_risk_reducing_exits] = 1),
            CONSTRAINT [ck_reconciliation_discrepancies_resolution]
                CHECK
                (
                    ([status] IN ('OPEN', 'CONFIRMED') AND [resolved_at_utc] IS NULL)
                    OR ([status] IN ('RESOLVED', 'IGNORED') AND [resolved_at_utc] IS NOT NULL)
                ),
            CONSTRAINT [ck_reconciliation_discrepancies_local_json]
                CHECK ([local_value_json] IS NULL OR ISJSON([local_value_json]) = 1),
            CONSTRAINT [ck_reconciliation_discrepancies_broker_json]
                CHECK ([broker_value_json] IS NULL OR ISJSON([broker_value_json]) = 1)
        );

        CREATE INDEX [ix_reconciliation_discrepancies_open]
            ON [execution].[reconciliation_discrepancies]
            ([status], [severity], [detected_at_utc] DESC)
            INCLUDE ([reconciliation_run_id], [order_id], [blocks_new_exposure]);
    END;

    IF OBJECT_ID(N'[execution].[reconciliation_resolutions]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[reconciliation_resolutions]
        (
            [reconciliation_resolution_id] bigint IDENTITY(1,1) NOT NULL,
            [reconciliation_resolution_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_reconciliation_resolutions_uid] DEFAULT NEWSEQUENTIALID(),
            [reconciliation_discrepancy_id] bigint NOT NULL,
            [resolution_sequence] int NOT NULL,
            [resolution_action] varchar(40) NOT NULL,
            [resolution_reason_code] varchar(100) NOT NULL,
            [resolution_notes] nvarchar(2000) NOT NULL,
            [resulting_order_event_id] bigint NULL,
            [approved_by] nvarchar(256) NOT NULL,
            [resolved_at_utc] datetime2(7) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_reconciliation_resolutions_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_reconciliation_resolutions]
                PRIMARY KEY CLUSTERED ([reconciliation_resolution_id]),
            CONSTRAINT [uq_reconciliation_resolutions_uid]
                UNIQUE ([reconciliation_resolution_uid]),
            CONSTRAINT [uq_reconciliation_resolutions_sequence]
                UNIQUE ([reconciliation_discrepancy_id], [resolution_sequence]),
            CONSTRAINT [fk_reconciliation_resolutions_discrepancy]
                FOREIGN KEY ([reconciliation_discrepancy_id])
                REFERENCES [execution].[reconciliation_discrepancies]
                    ([reconciliation_discrepancy_id]),
            CONSTRAINT [fk_reconciliation_resolutions_order_event]
                FOREIGN KEY ([resulting_order_event_id])
                REFERENCES [execution].[order_events] ([order_event_id]),
            CONSTRAINT [ck_reconciliation_resolutions_sequence]
                CHECK ([resolution_sequence] >= 1),
            CONSTRAINT [ck_reconciliation_resolutions_action]
                CHECK
                (
                    [resolution_action] IN
                    ('NO_ACTION', 'ACCEPT_BROKER_STATE', 'APPLY_COMPENSATING_EVENT',
                     'CANCEL_DUPLICATE', 'OPERATOR_OVERRIDE', 'MARK_IRRECOVERABLE')
                ),
            CONSTRAINT [ck_reconciliation_resolutions_event]
                CHECK
                (
                    [resolution_action] <> 'APPLY_COMPENSATING_EVENT'
                    OR [resulting_order_event_id] IS NOT NULL
                ),
            CONSTRAINT [ck_reconciliation_resolutions_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );
    END;

    UPDATE [operations].[database_metadata]
    SET
        [schema_baseline_version] = 'V0007',
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
