SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_uid] = '08804147-6062-5bfa-bbf5-99231b5a5a2b'
      AND [engine_code] = 'THESIS_PULSE_THESIS_FUSION'
      AND [engine_role] = 'FUSION'
      AND [owner_service] = 'ThesisPulse.Thesis.Service'
      AND [can_create_signals] = 1
      AND [can_execute_orders] = 0
      AND [is_active] = 1
)
    THROW 71631, 'Authoritative Thesis Fusion signal creator is missing or drifted.', 1;

IF EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_code] = 'THESIS_PULSE_THESIS_FUSION'
      AND ([engine_role] <> 'FUSION' OR [can_create_signals] <> 1 OR [can_execute_orders] <> 0)
)
    THROW 71632, 'Thesis Fusion authority boundary is invalid.', 1;
