SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
BEGIN TRANSACTION;

IF OBJECT_ID(N'[intelligence].[engines]', N'U') IS NULL
    THROW 61631, 'V0004 intelligence engine table is required.', 1;

DECLARE @actor nvarchar(256) = N'ThesisPulse.Seed.S0016';
DECLARE @seed_time datetime2(7) = '2026-07-01T00:00:00';
DECLARE @engine_uid uniqueidentifier = '08804147-6062-5bfa-bbf5-99231b5a5a2b';
DECLARE @engine_code varchar(100) = 'THESIS_PULSE_THESIS_FUSION';

IF NOT EXISTS
(
    SELECT 1 FROM [intelligence].[engines]
    WHERE [engine_code] = @engine_code
)
BEGIN
    INSERT INTO [intelligence].[engines]
    (
        [engine_uid], [engine_code], [engine_name], [engine_role],
        [owner_service], [can_create_signals], [can_execute_orders],
        [is_active], [created_at_utc], [created_by], [updated_at_utc], [updated_by]
    )
    VALUES
    (
        @engine_uid, @engine_code, N'ThesisPulse Deterministic Thesis Fusion', 'FUSION',
        'ThesisPulse.Thesis.Service', 1, 0,
        1, @seed_time, @actor, @seed_time, @actor
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM [intelligence].[engines]
    WHERE [engine_uid] = @engine_uid
      AND [engine_code] = @engine_code
      AND [engine_role] = 'FUSION'
      AND [owner_service] = 'ThesisPulse.Thesis.Service'
      AND [can_create_signals] = 1
      AND [can_execute_orders] = 0
      AND [is_active] = 1
)
    THROW 61632, 'Thesis Fusion signal creator seed drift detected.', 1;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
