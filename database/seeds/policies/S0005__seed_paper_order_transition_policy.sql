SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
BEGIN TRANSACTION;

IF OBJECT_ID(N'[execution].[order_transition_policies]', N'U') IS NULL
   OR OBJECT_ID(N'[execution].[order_transition_rules]', N'U') IS NULL
    THROW 61501, 'V0007 order-transition tables are required.', 1;

DECLARE @actor nvarchar(256) = N'ThesisPulse.Seed.S0005';
DECLARE @seed_time datetime2(7) = '2026-06-29T00:00:00';
DECLARE @policy_uid uniqueidentifier = '64e6d942-ab4c-5dbf-b4aa-45610da1045b';
DECLARE @checksum char(64) = 'f7b4309f5abbd7b406178b7acb1f8b298562dfacab994596a184372a3a6a3626';
DECLARE @metadata nvarchar(max) = N'{"activation_scope":"PAPER_ONLY","live_use_prohibited":true,"review_required":true,"rule_count":0,"seed":"S0005"}';

IF NOT EXISTS
(
    SELECT 1 FROM [execution].[order_transition_policies]
    WHERE [policy_version] = 'order-transitions-1.0.0-draft'
      AND [environment] = 'PAPER'
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
        @policy_uid, 'order-transitions-1.0.0-draft', 'PAPER', 'DRAFT',
        '2026-01-01T00:00:00', NULL, @checksum,
        @seed_time, N'ThesisPulse Architecture Review', NULL, NULL,
        @metadata, @seed_time, @actor
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM [execution].[order_transition_policies]
    WHERE [order_transition_policy_uid] = @policy_uid
      AND [policy_version] = 'order-transitions-1.0.0-draft'
      AND [environment] = 'PAPER'
      AND [status] = 'DRAFT'
      AND [effective_from_utc] = '2026-01-01T00:00:00'
      AND [effective_to_utc] IS NULL
      AND [checksum] = @checksum
      AND [metadata_json] = @metadata
      AND [approved_at_utc] IS NULL
      AND [approved_by] IS NULL
)
    THROW 61502, 'PAPER draft order-transition policy seed drift detected.', 1;

DECLARE @policy_id bigint =
(
    SELECT [order_transition_policy_id]
    FROM [execution].[order_transition_policies]
    WHERE [order_transition_policy_uid] = @policy_uid
);

IF EXISTS
(
    SELECT 1 FROM [execution].[order_transition_rules]
    WHERE [order_transition_policy_id] = @policy_id
)
    THROW 61503, 'The draft transition policy must contain zero rules.', 1;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
