SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[market].[live_feed_subscriptions]', N'U') IS NULL
    THROW 61001, 'market.live_feed_subscriptions does not exist.', 1;

IF OBJECT_ID(N'[market].[data_gap_events]', N'U') IS NULL
    THROW 61002, 'market.data_gap_events does not exist.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[market].[live_feed_subscriptions]')
      AND [name] = N'ix_live_feed_subscriptions_active'
)
    THROW 61003, 'Active subscription index is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[market].[data_gap_events]')
      AND [name] = N'ix_data_gap_events_pending'
)
    THROW 61004, 'Pending gap index is missing.', 1;

PRINT 'Market data recovery schema verification passed.';
