SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'[market].[tr_candles_publish_v1]', N'TR') IS NULL
    THROW 61201, 'V0011 candle publication trigger is required.', 1;
GO

DISABLE TRIGGER [market].[tr_candles_publish_v1]
ON [market].[candles];
GO

PRINT 'Market Data candle publication is installed but disabled by default.';
GO
