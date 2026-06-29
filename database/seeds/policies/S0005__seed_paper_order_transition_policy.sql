SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
BEGIN TRANSACTION;

IF OBJECT_ID(N'[execution].[order_transition_policies]', N'U') IS NULL
    THROW 61501, 'V0007 order-transition policy table is required.', 1;

DECLARE @actor nvarchar(256) = N'ThesisPulse.Seed.S0005';
DECLARE @reviewer nvarchar(200) = N'ThesisPulse Architecture Review';
DECLARE @seed_time datetime2(7) = '2026-06-29T00:00:00';
DECLARE @policy_uid uniqueidentifier = '551a80e5-685c-5bed-9a22-405a299d248f';
DECLARE @checksum char(64) = '782cec6f2e4be39102be9090d1dccaf00571f3348443ec61f7e548463983703f';
DECLARE @metadata nvarchar(max) = N'{"activation_scope":"PAPER_ONLY","live_use_prohibited":true,"rule_count":36,"seed":"S0005"}';

IF NOT EXISTS
(
    SELECT 1 FROM [execution].[order_transition_policies]
    WHERE [policy_version] = 'order-transitions-1.0.0' AND [environment] = 'PAPER'
)
BEGIN
    INSERT INTO [execution].[order_transition_policies]
    (
        [order_transition_policy_uid], [policy_version], [environment], [status],
        [effective_from_utc], [effective_to_utc], [checksum],
        [created_at_utc], [created_by], [approved_at_utc], [approved_by],
        [metadata_json], [created_record_at_utc], [created_record_by]
    )
    VALUES
    (
        @policy_uid, 'order-transitions-1.0.0', 'PAPER', 'ACTIVE',
        '2026-01-01T00:00:00', NULL, @checksum,
        @seed_time, @reviewer, @seed_time, @reviewer,
        @metadata, @seed_time, @actor
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM [execution].[order_transition_policies]
    WHERE [order_transition_policy_uid] = @policy_uid
      AND [policy_version] = 'order-transitions-1.0.0'
      AND [environment] = 'PAPER'
      AND [status] = 'ACTIVE'
      AND [effective_from_utc] = '2026-01-01T00:00:00'
      AND [effective_to_utc] IS NULL
      AND [checksum] = @checksum
      AND [metadata_json] = @metadata
)
    THROW 61502, 'PAPER order-transition policy seed drift detected.', 1;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
