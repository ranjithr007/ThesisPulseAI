SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
BEGIN TRANSACTION;

IF OBJECT_ID(N'[reference].[brokers]', N'U') IS NULL
    THROW 61301, 'V0002 reference.brokers is required.', 1;

DECLARE @actor nvarchar(256) = N'ThesisPulse.Seed.S0003';
DECLARE @seed_time datetime2(7) = '2026-06-29T00:00:00';
DECLARE @broker_uid uniqueidentifier = '625db6d6-f5e9-5ed1-9f60-9d66e6f0e7de';

IF NOT EXISTS (SELECT 1 FROM [reference].[brokers] WHERE [broker_code] = 'SIMULATOR')
BEGIN
    INSERT INTO [reference].[brokers]
    (
        [broker_uid], [broker_code], [broker_name], [is_active],
        [created_at_utc], [created_by], [updated_at_utc], [updated_by]
    )
    VALUES
    (
        @broker_uid, 'SIMULATOR', N'ThesisPulse Paper Simulator', 1,
        @seed_time, @actor, @seed_time, @actor
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM [reference].[brokers]
    WHERE [broker_code] = 'SIMULATOR'
      AND [broker_uid] = @broker_uid
      AND [broker_name] = N'ThesisPulse Paper Simulator'
      AND [is_active] = 1
)
    THROW 61302, 'SIMULATOR broker seed drift detected.', 1;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
