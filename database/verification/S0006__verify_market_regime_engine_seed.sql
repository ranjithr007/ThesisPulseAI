SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[intelligence].[engines]', N'U') IS NULL
    THROW 63001, 'Required intelligence engine table is missing.', 1;

IF
(
    SELECT COUNT_BIG(*)
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_MARKET_REGIME'
      AND [engine_uid] = '5f2f3d4a-8c7b-5a62-9a13-427e0f8c1d77'
      AND [engine_role] = 'CONTEXT_PROVIDER'
      AND [owner_service] = 'ThesisPulse.AI'
      AND [can_create_signals] = 0
      AND [can_execute_orders] = 0
      AND [is_active] = 1
) <> 1
    THROW 63002, 'Market regime engine seed validation failed.', 1;

IF EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_MARKET_REGIME'
      AND ([can_create_signals] = 1 OR [can_execute_orders] = 1)
)
    THROW 63003, 'Market regime engine authority validation failed.', 1;

PRINT 'Market regime engine seed verification passed.';
