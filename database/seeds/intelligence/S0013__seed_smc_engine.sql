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
    THROW 611301, 'V0004 intelligence.engines is required.', 1;

DECLARE @actor nvarchar(256) = N'ThesisPulse.Seed.S0013';
DECLARE @seed_time datetime2(7) = '2026-06-30T00:00:00';
DECLARE @engine_uid uniqueidentifier = 'd25f6038-75a5-5b83-ae76-5f8fc097e9f3';
DECLARE @engine_code varchar(100) = 'THESIS_PULSE_SMC';

IF NOT EXISTS
(
    SELECT 1 FROM [intelligence].[engines] WHERE [engine_code] = @engine_code
)
BEGIN
    INSERT INTO [intelligence].[engines]
    (
        [engine_uid], [engine_code], [engine_name], [engine_role],
        [owner_service], [can_create_signals], [can_execute_orders], [is_active],
        [created_at_utc], [created_by], [updated_at_utc], [updated_by]
    )
    VALUES
    (
        @engine_uid,
        @engine_code,
        N'ThesisPulse Smart Money Concepts Engine',
        'DIRECTIONAL_VOTER',
        'ThesisPulse.AI',
        0,
        0,
        1,
        @seed_time,
        @actor,
        @seed_time,
        @actor
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [intelligence].[engines]
    WHERE [engine_uid] = @engine_uid
      AND [engine_code] = @engine_code
      AND [engine_role] = 'DIRECTIONAL_VOTER'
      AND [owner_service] = 'ThesisPulse.AI'
      AND [can_create_signals] = 0
      AND [can_execute_orders] = 0
      AND [is_active] = 1
)
    THROW 611302, 'SMC engine seed drift detected.', 1;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
GO
