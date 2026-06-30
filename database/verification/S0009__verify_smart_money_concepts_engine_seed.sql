SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[intelligence].[engines]', N'U') IS NULL
    THROW 621301, 'intelligence.engines is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_SMART_MONEY_CONCEPTS'
      AND [engine_role] = 'DIRECTIONAL_VOTER'
      AND [owner_service] = 'ThesisPulse.AI'
      AND [can_create_signals] = 0
      AND [can_execute_orders] = 0
      AND [is_active] = 1
)
    THROW 621302, 'Smart Money Concepts engine authority is invalid.', 1;

IF EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_SMART_MONEY_CONCEPTS'
      AND ([can_create_signals] = 1 OR [can_execute_orders] = 1)
)
    THROW 621303, 'Smart Money Concepts authority drift detected.', 1;

SELECT 'SMART_MONEY_CONCEPTS_ENGINE_SEED_OK' AS [verification_result];
