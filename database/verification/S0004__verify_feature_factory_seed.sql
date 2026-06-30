SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[intelligence].[engines]', N'U') IS NULL
    THROW 62801, 'intelligence.engines does not exist.', 1;

IF
(
    SELECT COUNT_BIG(*)
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_FEATURE_FACTORY'
      AND [engine_uid] = 'f4c975be-fdad-5ad0-a4bd-a2f2571a6941'
      AND [engine_role] = 'CONTEXT_PROVIDER'
      AND [owner_service] = 'ThesisPulse.AI'
      AND [can_create_signals] = 0
      AND [can_execute_orders] = 0
      AND [is_active] = 1
) <> 1
    THROW 62802, 'Feature Factory engine authority is invalid.', 1;

IF EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_FEATURE_FACTORY'
      AND ([can_create_signals] = 1 OR [can_execute_orders] = 1)
)
    THROW 62803, 'Feature Factory must not have signal or execution authority.', 1;

PRINT 'Feature Factory engine seed verification passed.';
