SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[intelligence].[engines]', N'U') IS NULL
    THROW 62901, 'Required intelligence engine table is missing.', 1;

IF
(
    SELECT COUNT_BIG(*)
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_TECHNICAL_DIRECTION'
      AND [engine_uid] = '38f3a9d4-6e57-5e85-93b7-1f55eab129e9'
      AND [engine_role] = 'DIRECTIONAL_VOTER'
      AND [owner_service] = 'ThesisPulse.AI'
      AND [can_create_signals] = 0
      AND [can_execute_orders] = 0
      AND [is_active] = 1
) <> 1
    THROW 62902, 'Directional engine seed validation failed.', 1;

IF EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_TECHNICAL_DIRECTION'
      AND ([can_create_signals] = 1 OR [can_execute_orders] = 1)
)
    THROW 62903, 'Directional engine authority validation failed.', 1;

PRINT 'Directional engine seed verification passed.';
