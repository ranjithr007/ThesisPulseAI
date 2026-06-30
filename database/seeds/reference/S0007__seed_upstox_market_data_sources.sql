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

IF OBJECT_ID(N'[reference].[brokers]', N'U') IS NULL
    THROW 61701, 'V0002 reference.brokers is required.', 1;

IF OBJECT_ID(N'[market].[data_sources]', N'U') IS NULL
    THROW 61702, 'V0003 market.data_sources is required.', 1;

DECLARE @actor nvarchar(256) = N'ThesisPulse.Seed.S0007';
DECLARE @seed_time datetime2(7) = '2026-06-30T00:00:00';
DECLARE @broker_uid uniqueidentifier = '830f6149-5ed2-50f1-b321-bf47d9705262';

IF NOT EXISTS
(
    SELECT 1
    FROM [reference].[brokers]
    WHERE [broker_code] = 'UPSTOX'
)
BEGIN
    INSERT INTO [reference].[brokers]
    (
        [broker_uid], [broker_code], [broker_name], [is_active],
        [created_at_utc], [created_by], [updated_at_utc], [updated_by]
    )
    VALUES
    (
        @broker_uid, 'UPSTOX', N'Upstox', 1,
        @seed_time, @actor, @seed_time, @actor
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [reference].[brokers]
    WHERE [broker_uid] = @broker_uid
      AND [broker_code] = 'UPSTOX'
      AND [is_active] = 1
)
    THROW 61703, 'Upstox broker seed drift detected.', 1;

DECLARE @sources TABLE
(
    [data_source_uid] uniqueidentifier NOT NULL,
    [source_code] varchar(50) NOT NULL,
    [source_name] nvarchar(200) NOT NULL,
    [transport_type] varchar(30) NOT NULL
);

INSERT INTO @sources
VALUES
    ('8d12295b-57ef-590b-9d25-174f7452c337', 'UPSTOX_REST', N'Upstox REST Market Data', 'REST'),
    ('17203f68-1c19-5923-8a04-471bf77fe083', 'UPSTOX_WS', N'Upstox WebSocket Market Feed', 'WEBSOCKET');

INSERT INTO [market].[data_sources]
(
    [data_source_uid], [source_code], [source_name], [source_type],
    [transport_type], [is_authoritative], [is_active],
    [created_at_utc], [created_by], [updated_at_utc], [updated_by]
)
SELECT
    source.[data_source_uid], source.[source_code], source.[source_name], 'BROKER',
    source.[transport_type], 1, 1,
    @seed_time, @actor, @seed_time, @actor
FROM @sources source
WHERE NOT EXISTS
(
    SELECT 1
    FROM [market].[data_sources] target
    WHERE target.[source_code] = source.[source_code]
);

IF EXISTS
(
    SELECT 1
    FROM @sources source
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM [market].[data_sources] target
        WHERE target.[data_source_uid] = source.[data_source_uid]
          AND target.[source_code] = source.[source_code]
          AND target.[source_type] = 'BROKER'
          AND target.[transport_type] = source.[transport_type]
          AND target.[is_authoritative] = 1
          AND target.[is_active] = 1
    )
)
    THROW 61704, 'Upstox market data source seed drift detected.', 1;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
