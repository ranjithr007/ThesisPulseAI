/*
Verification: V0002__verify_reference_tables.sql
Purpose:
  Verify the V0002 reference tables, keys, constraints and lookup indexes.
Expected result:
  One PASS result set and no raised verification error.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @expected_tables TABLE
(
    [table_name] sysname NOT NULL PRIMARY KEY
);

INSERT INTO @expected_tables ([table_name])
VALUES
    (N'exchanges'),
    (N'exchange_calendars'),
    (N'calendar_days'),
    (N'trading_sessions'),
    (N'instruments'),
    (N'universe_versions'),
    (N'universe_members'),
    (N'brokers'),
    (N'broker_instrument_mappings');

IF EXISTS
(
    SELECT 1
    FROM @expected_tables AS expected
    WHERE OBJECT_ID
    (
        N'[reference].' + QUOTENAME(expected.[table_name]),
        N'U'
    ) IS NULL
)
BEGIN
    SELECT expected.[table_name] AS [missing_table]
    FROM @expected_tables AS expected
    WHERE OBJECT_ID
    (
        N'[reference].' + QUOTENAME(expected.[table_name]),
        N'U'
    ) IS NULL;

    RAISERROR('V0002 reference table verification failed.', 16, 1);
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
    (N'[reference].[exchange_calendars]', N'fk_exchange_calendars_exchange'),
    (N'[reference].[calendar_days]', N'fk_calendar_days_calendar'),
    (N'[reference].[trading_sessions]', N'fk_trading_sessions_calendar'),
    (N'[reference].[instruments]', N'fk_instruments_exchange'),
    (N'[reference].[instruments]', N'fk_instruments_underlying'),
    (N'[reference].[universe_members]', N'fk_universe_members_version'),
    (N'[reference].[universe_members]', N'fk_universe_members_instrument'),
    (N'[reference].[broker_instrument_mappings]', N'fk_broker_instrument_mappings_broker'),
    (N'[reference].[broker_instrument_mappings]', N'fk_broker_instrument_mappings_instrument');

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
    )
)
BEGIN
    SELECT
        expected.[table_name],
        expected.[foreign_key_name] AS [missing_foreign_key]
    FROM @expected_foreign_keys AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[foreign_key_name]
    );

    RAISERROR('V0002 foreign-key verification failed.', 16, 1);
    RETURN;
END;

DECLARE @expected_indexes TABLE
(
    [table_name] nvarchar(300) NOT NULL,
    [index_name] sysname NOT NULL,
    [must_be_unique] bit NOT NULL,
    PRIMARY KEY ([table_name], [index_name])
);

INSERT INTO @expected_indexes ([table_name], [index_name], [must_be_unique])
VALUES
    (N'[reference].[exchange_calendars]', N'ux_exchange_calendars_active', 1),
    (N'[reference].[exchange_calendars]', N'ix_exchange_calendars_validity', 0),
    (N'[reference].[calendar_days]', N'ix_calendar_days_trading_date', 0),
    (N'[reference].[trading_sessions]', N'ix_trading_sessions_lookup', 0),
    (N'[reference].[instruments]', N'ix_instruments_active_symbol', 0),
    (N'[reference].[instruments]', N'ix_instruments_underlying_expiry', 0),
    (N'[reference].[universe_versions]', N'ux_universe_versions_active', 1),
    (N'[reference].[universe_members]', N'ix_universe_members_instrument', 0),
    (N'[reference].[broker_instrument_mappings]', N'ux_broker_instrument_mappings_open_instrument', 1),
    (N'[reference].[broker_instrument_mappings]', N'ux_broker_instrument_mappings_open_key', 1),
    (N'[reference].[broker_instrument_mappings]', N'ix_broker_instrument_mappings_lookup', 0);

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
          AND actual.[is_disabled] = 0
    );

    RAISERROR('V0002 index verification failed.', 16, 1);
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
    (N'[reference].[exchange_calendars]', N'ck_exchange_calendars_dates'),
    (N'[reference].[calendar_days]', N'ck_calendar_days_trading_flag'),
    (N'[reference].[trading_sessions]', N'ck_trading_sessions_times'),
    (N'[reference].[instruments]', N'ck_instruments_derivative_fields'),
    (N'[reference].[universe_versions]', N'ck_universe_versions_approval'),
    (N'[reference].[universe_members]', N'ck_universe_members_short'),
    (N'[reference].[broker_instrument_mappings]', N'ck_broker_instrument_mappings_metadata_json');

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

    RAISERROR('V0002 check-constraint verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [operations].[database_metadata]
    WHERE [database_metadata_id] = 1
      AND [schema_baseline_version] = 'V0002'
)
BEGIN
    RAISERROR('Database metadata was not advanced to V0002.', 16, 1);
    RETURN;
END;

SELECT
    'PASS' AS [verification_status],
    'V0002' AS [migration_version],
    DB_NAME() AS [database_name],
    (SELECT COUNT_BIG(*) FROM @expected_tables) AS [verified_table_count],
    (SELECT COUNT_BIG(*) FROM @expected_foreign_keys) AS [verified_foreign_key_count],
    (SELECT COUNT_BIG(*) FROM @expected_indexes) AS [verified_index_count],
    (SELECT COUNT_BIG(*) FROM @expected_checks) AS [verified_check_count];
