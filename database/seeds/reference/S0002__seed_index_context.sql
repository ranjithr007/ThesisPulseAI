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

IF OBJECT_ID(N'[reference].[instruments]', N'U') IS NULL
   OR OBJECT_ID(N'[reference].[universe_versions]', N'U') IS NULL
   OR OBJECT_ID(N'[reference].[universe_members]', N'U') IS NULL
    THROW 61201, 'V0002 reference tables are required.', 1;

DECLARE @actor nvarchar(256) = N'ThesisPulse.Seed.S0002';
DECLARE @seed_time datetime2(7) = '2026-06-29T00:00:00';
DECLARE @exchange_id bigint =
    (SELECT [exchange_id] FROM [reference].[exchanges] WHERE [exchange_code] = 'NSE');
IF @exchange_id IS NULL THROW 61202, 'S0001 is required.', 1;

DECLARE @source TABLE
(
    [uid] uniqueidentifier NOT NULL,
    [symbol] varchar(100) NOT NULL PRIMARY KEY,
    [name] nvarchar(200) NOT NULL
);
INSERT INTO @source VALUES
('48ba8468-cf21-513b-b432-f4426bdff2f8', 'NIFTY50', N'NIFTY 50'),
('9c5bc672-718d-53cb-b0a7-3b286e98e6ab', 'NIFTYBANK', N'NIFTY Bank'),
('bb99f680-951a-537b-934d-aa58dc07fd7a', 'FINNIFTY', N'NIFTY Financial Services');

INSERT INTO [reference].[instruments]
(
    [instrument_uid], [exchange_id], [canonical_symbol], [display_name],
    [instrument_type], [market_segment], [base_currency_code],
    [tick_size], [lot_size], [price_scale], [quantity_scale],
    [underlying_instrument_id], [expiry_date], [strike_price], [option_type],
    [status], [valid_from_date], [valid_to_date], [is_trade_allowed], [is_short_allowed],
    [created_at_utc], [created_by], [updated_at_utc], [updated_by]
)
SELECT
    s.[uid], @exchange_id, s.[symbol], s.[name],
    'INDEX', 'INDEX', 'INR', 0.050000, 1.000000, 2, 0,
    NULL, NULL, NULL, NULL,
    'ACTIVE', '2026-01-01', NULL, 0, 0,
    @seed_time, @actor, @seed_time, @actor
FROM @source s
WHERE NOT EXISTS
(
    SELECT 1 FROM [reference].[instruments] t
    WHERE t.[exchange_id] = @exchange_id
      AND t.[canonical_symbol] = s.[symbol]
      AND t.[valid_from_date] = '2026-01-01'
);

IF EXISTS
(
    SELECT 1 FROM @source s
    WHERE NOT EXISTS
    (
        SELECT 1 FROM [reference].[instruments] t
        WHERE t.[instrument_uid] = s.[uid]
          AND t.[exchange_id] = @exchange_id
          AND t.[canonical_symbol] = s.[symbol]
          AND t.[display_name] = s.[name]
          AND t.[instrument_type] = 'INDEX'
          AND t.[market_segment] = 'INDEX'
          AND t.[is_trade_allowed] = 0
          AND t.[is_short_allowed] = 0
          AND t.[valid_to_date] IS NULL
    )
)
    THROW 61203, 'Index context seed drift detected.', 1;

DECLARE @universe_uid uniqueidentifier = '3aa01fd7-38d7-5fa2-b23c-332f6c7c90dd';
IF NOT EXISTS
(
    SELECT 1 FROM [reference].[universe_versions]
    WHERE [universe_code] = 'TPAI_INDEX_CONTEXT'
      AND [universe_version] = '1.0.0'
      AND [environment] = 'RESEARCH'
)
BEGIN
    INSERT INTO [reference].[universe_versions]
    (
        [universe_uid], [universe_code], [universe_version], [environment], [status],
        [valid_from_date], [valid_to_date], [description], [approved_at_utc], [approved_by],
        [created_at_utc], [created_by], [updated_at_utc], [updated_by]
    )
    VALUES
    (
        @universe_uid, 'TPAI_INDEX_CONTEXT', '1.0.0', 'RESEARCH', 'ACTIVE',
        '2026-01-01', NULL, N'Research-only, non-tradable index context.',
        @seed_time, N'ThesisPulse Architecture Review',
        @seed_time, @actor, @seed_time, @actor
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM [reference].[universe_versions]
    WHERE [universe_uid] = @universe_uid
      AND [universe_code] = 'TPAI_INDEX_CONTEXT'
      AND [universe_version] = '1.0.0'
      AND [environment] = 'RESEARCH'
      AND [status] = 'ACTIVE'
)
    THROW 61204, 'Index universe seed drift detected.', 1;

DECLARE @version_id bigint =
(
    SELECT [universe_version_id] FROM [reference].[universe_versions]
    WHERE [universe_code] = 'TPAI_INDEX_CONTEXT'
      AND [universe_version] = '1.0.0'
      AND [environment] = 'RESEARCH'
);

INSERT INTO [reference].[universe_members]
(
    [universe_version_id], [instrument_id], [member_role], [allocation_weight],
    [is_trade_allowed], [is_short_allowed], [created_at_utc], [created_by]
)
SELECT @version_id, i.[instrument_id], 'BENCHMARK', NULL, 0, 0, @seed_time, @actor
FROM [reference].[instruments] i
JOIN @source s ON s.[uid] = i.[instrument_uid]
WHERE NOT EXISTS
(
    SELECT 1 FROM [reference].[universe_members] m
    WHERE m.[universe_version_id] = @version_id
      AND m.[instrument_id] = i.[instrument_id]
);

IF
(
    SELECT COUNT_BIG(*) FROM [reference].[universe_members]
    WHERE [universe_version_id] = @version_id
      AND [member_role] = 'BENCHMARK'
      AND [is_trade_allowed] = 0
      AND [is_short_allowed] = 0
) <> 3
    THROW 61205, 'Index universe membership is invalid.', 1;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
