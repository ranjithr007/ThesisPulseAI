SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS
(
    SELECT 1 FROM [reference].[exchanges]
    WHERE [exchange_uid]='ec67af1b-65ac-5763-9912-c92108748fe4'
      AND [exchange_code]='NSE'
      AND [timezone_id]='Asia/Kolkata'
      AND [currency_code]='INR'
      AND [is_active]=1
)
    THROW 61901, 'NSE seed verification failed.', 1;

DECLARE @calendar_id bigint =
(
    SELECT c.[exchange_calendar_id]
    FROM [reference].[exchange_calendars] c
    JOIN [reference].[exchanges] e ON e.[exchange_id]=c.[exchange_id]
    WHERE e.[exchange_code]='NSE'
      AND c.[calendar_version]='NSE-2026-DRAFT-1'
      AND c.[status]='DRAFT'
);
IF @calendar_id IS NULL THROW 61902, 'NSE draft calendar verification failed.', 1;
IF (SELECT COUNT_BIG(*) FROM [reference].[trading_sessions]
    WHERE [exchange_calendar_id]=@calendar_id AND [session_code]='REGULAR')<>4
    THROW 61903, 'NSE session verification failed.', 1;

IF
(
    SELECT COUNT_BIG(*) FROM [reference].[instruments]
    WHERE [instrument_uid] IN
    (
        '48ba8468-cf21-513b-b432-f4426bdff2f8',
        '9c5bc672-718d-53cb-b0a7-3b286e98e6ab',
        'bb99f680-951a-537b-934d-aa58dc07fd7a'
    )
      AND [instrument_type]='INDEX'
      AND [is_trade_allowed]=0
      AND [is_short_allowed]=0
)<>3
    THROW 61904, 'Index context verification failed.', 1;

DECLARE @universe_id bigint =
(
    SELECT [universe_version_id] FROM [reference].[universe_versions]
    WHERE [universe_uid]='3aa01fd7-38d7-5fa2-b23c-332f6c7c90dd'
      AND [universe_code]='TPAI_INDEX_CONTEXT'
      AND [universe_version]='1.0.0'
      AND [environment]='RESEARCH'
      AND [status]='ACTIVE'
);
IF @universe_id IS NULL THROW 61905, 'Index universe verification failed.', 1;
IF (SELECT COUNT_BIG(*) FROM [reference].[universe_members]
    WHERE [universe_version_id]=@universe_id
      AND [is_trade_allowed]=0 AND [is_short_allowed]=0)<>3
    THROW 61906, 'Index universe member verification failed.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [reference].[brokers]
    WHERE [broker_uid]='625db6d6-f5e9-5ed1-9f60-9d66e6f0e7de'
      AND [broker_code]='SIMULATOR'
      AND [is_active]=1
)
    THROW 61907, 'SIMULATOR seed verification failed.', 1;

SELECT 'PASS' AS [verification_status], 'REFERENCE' AS [seed_domain],
       3 AS [context_instruments], 3 AS [universe_members], 1 AS [draft_calendar];
