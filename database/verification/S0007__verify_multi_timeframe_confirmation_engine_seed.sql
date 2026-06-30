SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[intelligence].[engines]', N'U') IS NULL
    THROW 621101, 'Required intelligence engine table is missing.', 1;

IF
(
    SELECT COUNT_BIG(*)
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_MULTI_TIMEFRAME_CONFIRMATION'
      AND [engine_uid] = 'b0f95f26-5973-56cb-8b46-30667198f7a1'
      AND [engine_role] = 'META_CONTROLLER'
      AND [owner_service] = 'ThesisPulse.AI'
      AND [can_create_signals] = 0
      AND [can_execute_orders] = 0
      AND [is_active] = 1
) <> 1
    THROW 621102, 'Confirmation engine seed validation failed.', 1;

IF EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_MULTI_TIMEFRAME_CONFIRMATION'
      AND ([can_create_signals] = 1 OR [can_execute_orders] = 1)
)
    THROW 621103, 'Confirmation engine authority validation failed.', 1;

PRINT 'Multi-timeframe confirmation engine seed verification passed.';
