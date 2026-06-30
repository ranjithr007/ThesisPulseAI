SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[intelligence].[engines]', N'U') IS NULL
    THROW 621201, 'intelligence.engines is missing.', 1;

IF OBJECT_ID(N'[intelligence].[engine_output_message_inputs]', N'U') IS NULL
    THROW 621202, 'intelligence.engine_output_message_inputs is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_ORDER_FLOW'
      AND [engine_role] = 'DIRECTIONAL_VOTER'
      AND [owner_service] = 'ThesisPulse.AI'
      AND [can_create_signals] = 0
      AND [can_execute_orders] = 0
      AND [is_active] = 1
)
    THROW 621203, 'Order Flow engine authority is invalid.', 1;

IF EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_ORDER_FLOW'
      AND ([can_create_signals] = 1 OR [can_execute_orders] = 1)
)
    THROW 621204, 'Order Flow engine authority drift detected.', 1;

SELECT 'ORDER_FLOW_ENGINE_SEED_OK' AS [verification_result];
