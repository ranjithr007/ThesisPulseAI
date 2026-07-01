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
    THROW 611501, 'V0004 intelligence.engines is required.', 1;

DECLARE @actor nvarchar(256) = N'ThesisPulse.Seed.S0015';
DECLARE @seed_time datetime2(7) = '2026-07-01T00:00:00';
DECLARE @engine_uid uniqueidentifier = 'ca80672a-51eb-54b4-8e6c-5f366ca8ad3a';
DECLARE @engine_code varchar(100) =
    'THESIS_PULSE_OPTION_CHAIN_INTELLIGENCE';

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
        N'ThesisPulse Deterministic Option-Chain Intelligence Engine',
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
    THROW 611502, 'Option-Chain Intelligence engine seed drift detected.', 1;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
GO
