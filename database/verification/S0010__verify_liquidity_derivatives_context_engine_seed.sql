SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[intelligence].[engines]', N'U') IS NULL
    THROW 621401, 'intelligence.engines is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_LIQUIDITY_DERIVATIVES_CONTEXT'
      AND [engine_role] = 'DIRECTIONAL_VOTER'
      AND [owner_service] = 'ThesisPulse.AI'
      AND [can_create_signals] = 0
      AND [can_execute_orders] = 0
      AND [is_active] = 1
)
    THROW 621402, 'Liquidity Derivatives engine authority is invalid.', 1;

IF EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_LIQUIDITY_DERIVATIVES_CONTEXT'
      AND ([can_create_signals] = 1 OR [can_execute_orders] = 1)
)
    THROW 621403, 'Liquidity Derivatives authority drift detected.', 1;

SELECT 'LIQUIDITY_DERIVATIVES_CONTEXT_ENGINE_SEED_OK'
    AS [verification_result];
