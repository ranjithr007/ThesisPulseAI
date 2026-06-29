/*
Verification: V0007__verify_execution_and_reconciliation_tables.sql
Purpose:
  Verify V0007 broker-account, transition-policy, command, order, fill,
  broker-request and reconciliation tables, trusted relationships, filtered
  uniqueness, contract checks, fixed precision and the V0007 baseline marker.
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
    [schema_name] sysname NOT NULL,
    [table_name] sysname NOT NULL,
    PRIMARY KEY ([schema_name], [table_name])
);

INSERT INTO @expected_tables ([schema_name], [table_name])
VALUES
    (N'broker', N'broker_accounts'),
    (N'execution', N'order_transition_policies'),
    (N'execution', N'order_transition_rules'),
    (N'execution', N'execution_commands'),
    (N'execution', N'execution_command_events'),
    (N'execution', N'execution_command_states'),
    (N'execution', N'orders'),
    (N'execution', N'order_events'),
    (N'execution', N'order_event_quarantines'),
    (N'execution', N'fills'),
    (N'broker', N'broker_requests'),
    (N'execution', N'reconciliation_runs'),
    (N'execution', N'reconciliation_observations'),
    (N'execution', N'reconciliation_discrepancies'),
    (N'execution', N'reconciliation_resolutions');

IF EXISTS
(
    SELECT 1
    FROM @expected_tables AS expected
    WHERE OBJECT_ID
    (
        QUOTENAME(expected.[schema_name]) + N'.' + QUOTENAME(expected.[table_name]),
        N'U'
    ) IS NULL
)
BEGIN
    SELECT
        expected.[schema_name],
        expected.[table_name] AS [missing_table]
    FROM @expected_tables AS expected
    WHERE OBJECT_ID
    (
        QUOTENAME(expected.[schema_name]) + N'.' + QUOTENAME(expected.[table_name]),
        N'U'
    ) IS NULL;

    RAISERROR('V0007 table verification failed.', 16, 1);
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
    (N'[broker].[broker_accounts]', N'fk_broker_accounts_broker'),
    (N'[execution].[order_transition_rules]', N'fk_order_transition_rules_policy'),
    (N'[execution].[execution_commands]', N'fk_execution_commands_trade_plan'),
    (N'[execution].[execution_commands]', N'fk_execution_commands_broker_account'),
    (N'[execution].[execution_commands]', N'fk_execution_commands_instrument'),
    (N'[execution].[execution_commands]', N'fk_execution_commands_target_order'),
    (N'[execution].[execution_command_events]', N'fk_execution_command_events_command'),
    (N'[execution].[execution_command_states]', N'fk_execution_command_states_command'),
    (N'[execution].[execution_command_states]', N'fk_execution_command_states_result_order'),
    (N'[execution].[orders]', N'fk_orders_place_command'),
    (N'[execution].[orders]', N'fk_orders_trade_plan'),
    (N'[execution].[orders]', N'fk_orders_broker_account'),
    (N'[execution].[orders]', N'fk_orders_instrument'),
    (N'[execution].[order_events]', N'fk_order_events_order'),
    (N'[execution].[order_events]', N'fk_order_events_command'),
    (N'[execution].[order_events]', N'fk_order_events_trade_plan'),
    (N'[execution].[order_events]', N'fk_order_events_broker_account'),
    (N'[execution].[order_event_quarantines]', N'fk_order_event_quarantines_event'),
    (N'[execution].[order_event_quarantines]', N'fk_order_event_quarantines_policy'),
    (N'[execution].[fills]', N'fk_fills_order'),
    (N'[execution].[fills]', N'fk_fills_command'),
    (N'[execution].[fills]', N'fk_fills_trade_plan'),
    (N'[execution].[fills]', N'fk_fills_broker_account'),
    (N'[broker].[broker_requests]', N'fk_broker_requests_command'),
    (N'[broker].[broker_requests]', N'fk_broker_requests_account'),
    (N'[execution].[reconciliation_runs]', N'fk_reconciliation_runs_account'),
    (N'[execution].[reconciliation_observations]', N'fk_reconciliation_observations_run'),
    (N'[execution].[reconciliation_observations]', N'fk_reconciliation_observations_request'),
    (N'[execution].[reconciliation_observations]', N'fk_reconciliation_observations_order'),
    (N'[execution].[reconciliation_observations]', N'fk_reconciliation_observations_command'),
    (N'[execution].[reconciliation_discrepancies]', N'fk_reconciliation_discrepancies_run'),
    (N'[execution].[reconciliation_discrepancies]', N'fk_reconciliation_discrepancies_order'),
    (N'[execution].[reconciliation_discrepancies]', N'fk_reconciliation_discrepancies_command'),
    (N'[execution].[reconciliation_resolutions]', N'fk_reconciliation_resolutions_discrepancy'),
    (N'[execution].[reconciliation_resolutions]', N'fk_reconciliation_resolutions_order_event');

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

    RAISERROR('V0007 foreign-key verification failed.', 16, 1);
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
    (N'[broker].[broker_accounts]', N'ix_broker_accounts_status', 0, 0),
    (N'[execution].[order_transition_policies]', N'ix_order_transition_policies_effective', 0, 0),
    (N'[execution].[execution_commands]', N'ux_execution_commands_client_order', 1, 1),
    (N'[execution].[execution_commands]', N'ix_execution_commands_trade_plan', 0, 0),
    (N'[execution].[execution_commands]', N'ix_execution_commands_target_order', 0, 1),
    (N'[execution].[execution_commands]', N'ix_execution_commands_correlation', 0, 0),
    (N'[execution].[execution_command_events]', N'ix_execution_command_events_latest', 0, 0),
    (N'[execution].[orders]', N'ux_orders_broker_order', 1, 1),
    (N'[execution].[orders]', N'ix_orders_status', 0, 0),
    (N'[execution].[orders]', N'ix_orders_trade_plan', 0, 0),
    (N'[execution].[order_events]', N'ux_order_events_broker_sequence', 1, 1),
    (N'[execution].[order_events]', N'ix_order_events_order_time', 0, 0),
    (N'[execution].[order_events]', N'ix_order_events_projection', 0, 0),
    (N'[execution].[order_event_quarantines]', N'ix_order_event_quarantines_open', 0, 0),
    (N'[execution].[fills]', N'ux_fills_broker_fill', 1, 1),
    (N'[execution].[fills]', N'ux_fills_fingerprint', 1, 1),
    (N'[execution].[fills]', N'ix_fills_order_time', 0, 0),
    (N'[broker].[broker_requests]', N'ix_broker_requests_command', 0, 0),
    (N'[broker].[broker_requests]', N'ix_broker_requests_unknown', 0, 0),
    (N'[execution].[reconciliation_runs]', N'ix_reconciliation_runs_account', 0, 0),
    (N'[execution].[reconciliation_observations]', N'ix_reconciliation_observations_run', 0, 0),
    (N'[execution].[reconciliation_discrepancies]', N'ix_reconciliation_discrepancies_open', 0, 0);

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

    RAISERROR('V0007 index verification failed.', 16, 1);
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
    (N'[broker].[broker_accounts]', N'ck_broker_accounts_environment'),
    (N'[broker].[broker_accounts]', N'ck_broker_accounts_status'),
    (N'[broker].[broker_accounts]', N'ck_broker_accounts_exit_safety'),
    (N'[execution].[order_transition_policies]', N'ck_order_transition_policies_status'),
    (N'[execution].[order_transition_policies]', N'ck_order_transition_policies_approval'),
    (N'[execution].[order_transition_rules]', N'ck_order_transition_rules_event_type'),
    (N'[execution].[order_transition_rules]', N'ck_order_transition_rules_terminal'),
    (N'[execution].[execution_commands]', N'ck_execution_commands_contract_version'),
    (N'[execution].[execution_commands]', N'ck_execution_commands_type'),
    (N'[execution].[execution_commands]', N'ck_execution_commands_place_fields'),
    (N'[execution].[execution_commands]', N'ck_execution_commands_modify_fields'),
    (N'[execution].[execution_commands]', N'ck_execution_commands_cancel_fields'),
    (N'[execution].[execution_commands]', N'ck_execution_commands_price_fields'),
    (N'[execution].[execution_commands]', N'ck_execution_commands_validity'),
    (N'[execution].[execution_command_events]', N'ck_execution_command_events_type'),
    (N'[execution].[execution_command_events]', N'ck_execution_command_events_outcome'),
    (N'[execution].[execution_command_states]', N'ck_execution_command_states_retry_safety'),
    (N'[execution].[execution_command_states]', N'ck_execution_command_states_unknown_outcome'),
    (N'[execution].[orders]', N'ck_orders_quantities'),
    (N'[execution].[orders]', N'ck_orders_average_fill'),
    (N'[execution].[orders]', N'ck_orders_price_fields'),
    (N'[execution].[orders]', N'ck_orders_terminal'),
    (N'[execution].[orders]', N'ck_orders_reconciliation_status'),
    (N'[execution].[order_events]', N'ck_order_events_event_type'),
    (N'[execution].[order_events]', N'ck_order_events_normalized_status'),
    (N'[execution].[order_events]', N'ck_order_events_quantities'),
    (N'[execution].[order_events]', N'ck_order_events_projection_disposition'),
    (N'[execution].[order_events]', N'ck_order_events_reconciled_type'),
    (N'[execution].[order_event_quarantines]', N'ck_order_event_quarantines_resolution'),
    (N'[execution].[fills]', N'ck_fills_contract_version'),
    (N'[execution].[fills]', N'ck_fills_identity'),
    (N'[execution].[fills]', N'ck_fills_values'),
    (N'[broker].[broker_requests]', N'ck_broker_requests_operation'),
    (N'[broker].[broker_requests]', N'ck_broker_requests_completion'),
    (N'[broker].[broker_requests]', N'ck_broker_requests_unknown_outcome'),
    (N'[execution].[reconciliation_runs]', N'ck_reconciliation_runs_trigger'),
    (N'[execution].[reconciliation_runs]', N'ck_reconciliation_runs_completion'),
    (N'[execution].[reconciliation_observations]', N'ck_reconciliation_observations_type'),
    (N'[execution].[reconciliation_discrepancies]', N'ck_reconciliation_discrepancies_type'),
    (N'[execution].[reconciliation_discrepancies]', N'ck_reconciliation_discrepancies_exit_safety'),
    (N'[execution].[reconciliation_discrepancies]', N'ck_reconciliation_discrepancies_resolution'),
    (N'[execution].[reconciliation_resolutions]', N'ck_reconciliation_resolutions_action'),
    (N'[execution].[reconciliation_resolutions]', N'ck_reconciliation_resolutions_event');

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

    RAISERROR('V0007 check-constraint verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[execution].[execution_commands]')
      AND columns.[name] IN (N'quantity', N'limit_price', N'trigger_price')
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 3
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
    RAISERROR('V0007 command numeric precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[execution].[orders]')
      AND columns.[name] IN
      (
          N'requested_quantity', N'filled_quantity', N'remaining_quantity',
          N'average_fill_price', N'limit_price', N'trigger_price'
      )
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 6
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
    RAISERROR('V0007 order numeric precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS columns
    INNER JOIN sys.types AS types
        ON columns.[user_type_id] = types.[user_type_id]
    WHERE columns.[object_id] = OBJECT_ID(N'[execution].[fills]')
      AND columns.[name] IN
      (
          N'fill_quantity', N'fill_price', N'gross_amount',
          N'fees_amount', N'taxes_amount', N'net_amount'
      )
    GROUP BY columns.[object_id]
    HAVING COUNT_BIG(*) = 6
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
    RAISERROR('V0007 fill numeric precision verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [operations].[database_metadata]
    WHERE [database_metadata_id] = 1
      AND [schema_baseline_version] = 'V0007'
)
BEGIN
    RAISERROR('Database metadata was not advanced to V0007.', 16, 1);
    RETURN;
END;

SELECT
    'PASS' AS [verification_status],
    'V0007' AS [migration_version],
    DB_NAME() AS [database_name],
    (SELECT COUNT_BIG(*) FROM @expected_tables) AS [verified_table_count],
    (SELECT COUNT_BIG(*) FROM @expected_foreign_keys) AS [verified_foreign_key_count],
    (SELECT COUNT_BIG(*) FROM @expected_indexes) AS [verified_index_count],
    (SELECT COUNT_BIG(*) FROM @expected_checks) AS [verified_check_count];
