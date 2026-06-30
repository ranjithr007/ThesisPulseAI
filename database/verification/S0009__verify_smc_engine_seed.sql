SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[intelligence].[engines]', N'U') IS NULL
    THROW 621301, 'intelligence.engines is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_SMC'
      AND [engine_role] = 'DIRECTIONAL_VOTER'
      AND [owner_service] = 'ThesisPulse.AI'
      AND [can_create_signals] = 0
      AND [can_execute_orders] = 0
      AND [is_active] = 1
)
    THROW 621302, 'SMC engine authority is invalid.', 1;

IF EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_SMC'
      AND ([can_create_signals] = 1 OR [can_execute_orders] = 1)
)
    THROW 621303, 'SMC engine authority drift detected.', 1;

SELECT 'SMC_ENGINE_SEED_OK' AS [verification_result];
