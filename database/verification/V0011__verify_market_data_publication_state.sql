SET NOCOUNT ON;

SELECT
    OBJECT_ID(N'[operations].[consumer_checkpoints]', N'U') AS consumer_checkpoints_object_id,
    OBJECT_ID(N'[market].[tr_candles_publish_v1]', N'TR') AS candle_publication_object_id;

PRINT 'Market data publication objects inspected.';
