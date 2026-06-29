SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[intelligence].[engines]', N'U') IS NULL
    THROW 62601, 'intelligence.engines does not exist.', 1;

IF
(
    SELECT COUNT_BIG(*)
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_MOCK_FUSION'
      AND [engine_role] = 'FUSION'
      AND [owner_service] = 'ThesisPulse.AI'
      AND [can_create_signals] = 1
      AND [can_execute_orders] = 0
      AND [is_active] = 1
) <> 1
    THROW 62602, 'Mock fusion signal authority is invalid.', 1;

PRINT 'Mock fusion signal authority verification passed.';
