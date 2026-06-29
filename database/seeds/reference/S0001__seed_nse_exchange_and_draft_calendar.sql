/* S0001: NSE identity and a deliberately non-active 2026 calendar shell. */
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

    IF OBJECT_ID(N'[reference].[exchanges]', N'U') IS NULL
       OR OBJECT_ID(N'[reference].[exchange_calendars]', N'U') IS NULL
       OR OBJECT_ID(N'[reference].[trading_sessions]', N'U') IS NULL
        THROW 61101, 'V0002 reference calendar tables are required.', 1;

    DECLARE @actor nvarchar(256) = N'ThesisPulse.Seed.S0001';
    DECLARE @seed_time datetime2(7) = '2026-06-29T00:00:00';
    DECLARE @nse_uid uniqueidentifier = 'ec67af1b-65ac-5763-9912-c92108748fe4';

    IF NOT EXISTS (SELECT 1 FROM [reference].[exchanges] WHERE [exchange_code] = 'NSE')
    BEGIN
        INSERT INTO [reference].[exchanges]
        (
            [exchange_uid], [exchange_code], [exchange_name], [country_code],
            [timezone_id], [currency_code], [is_active],
            [created_at_utc], [created_by], [updated_at_utc], [updated_by]
        )
        VALUES
        (
            @nse_uid, 'NSE', N'National Stock Exchange of India', 'IN',
            'Asia/Kolkata', 'INR', 1,
            @seed_time, @actor, @seed_time, @actor
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM [reference].[exchanges]
        WHERE [exchange_code] = 'NSE'
          AND [exchange_uid] = @nse_uid
          AND [exchange_name] = N'National Stock Exchange of India'
          AND [country_code] = 'IN'
          AND [timezone_id] = 'Asia/Kolkata'
          AND [currency_code] = 'INR'
          AND [is_active] = 1
    )
        THROW 61102, 'Seed drift detected for NSE exchange identity.', 1;

    DECLARE @exchange_id bigint =
        (SELECT [exchange_id] FROM [reference].[exchanges] WHERE [exchange_code] = 'NSE');

    IF NOT EXISTS
    (
        SELECT 1 FROM [reference].[exchange_calendars]
        WHERE [exchange_id] = @exchange_id
          AND [calendar_version] = 'NSE-2026-DRAFT-1'
    )
    BEGIN
        INSERT INTO [reference].[exchange_calendars]
        (
            [exchange_id], [calendar_version], [timezone_id],
            [valid_from_date], [valid_to_date], [status], [description],
            [created_at_utc], [created_by], [updated_at_utc], [updated_by]
        )
        VALUES
        (
            @exchange_id, 'NSE-2026-DRAFT-1', 'Asia/Kolkata',
            '2026-01-01', '2026-12-31', 'DRAFT',
            N'Draft regular-session shell. Holidays and special sessions require review before activation.',
            @seed_time, @actor, @seed_time, @actor
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM [reference].[exchange_calendars]
        WHERE [exchange_id] = @exchange_id
          AND [calendar_version] = 'NSE-2026-DRAFT-1'
          AND [timezone_id] = 'Asia/Kolkata'
          AND [valid_from_date] = '2026-01-01'
          AND [valid_to_date] = '2026-12-31'
          AND [status] = 'DRAFT'
    )
        THROW 61103, 'Seed drift detected for NSE draft calendar.', 1;

    DECLARE @calendar_id bigint =
    (
        SELECT [exchange_calendar_id]
        FROM [reference].[exchange_calendars]
        WHERE [exchange_id] = @exchange_id
          AND [calendar_version] = 'NSE-2026-DRAFT-1'
    );

    DECLARE @sessions TABLE
    (
        [market_segment] varchar(30) PRIMARY KEY,
        [start_time_local] time(7) NOT NULL,
        [end_time_local] time(7) NOT NULL
    );

    INSERT INTO @sessions VALUES
        ('CASH', '09:15:00', '15:30:00'),
        ('INDEX', '09:15:00', '15:30:00'),
        ('FUTURES', '09:15:00', '15:30:00'),
        ('OPTIONS', '09:15:00', '15:30:00');

    INSERT INTO [reference].[trading_sessions]
    (
        [exchange_calendar_id], [market_segment], [session_code],
        [valid_from_date], [valid_to_date], [start_time_local], [end_time_local],
        [crosses_midnight], [is_order_entry_allowed], [is_market_data_expected],
        [created_at_utc], [created_by], [updated_at_utc], [updated_by]
    )
    SELECT
        @calendar_id, source.[market_segment], 'REGULAR',
        '2026-01-01', '2026-12-31', source.[start_time_local], source.[end_time_local],
        0, 1, 1, @seed_time, @actor, @seed_time, @actor
    FROM @sessions AS source
    WHERE NOT EXISTS
    (
        SELECT 1 FROM [reference].[trading_sessions] AS target
        WHERE target.[exchange_calendar_id] = @calendar_id
          AND target.[market_segment] = source.[market_segment]
          AND target.[session_code] = 'REGULAR'
          AND target.[valid_from_date] = '2026-01-01'
    );

    IF EXISTS
    (
        SELECT 1 FROM @sessions AS source
        WHERE NOT EXISTS
        (
            SELECT 1 FROM [reference].[trading_sessions] AS target
            WHERE target.[exchange_calendar_id] = @calendar_id
              AND target.[market_segment] = source.[market_segment]
              AND target.[session_code] = 'REGULAR'
              AND target.[valid_from_date] = '2026-01-01'
              AND target.[valid_to_date] = '2026-12-31'
              AND target.[start_time_local] = source.[start_time_local]
              AND target.[end_time_local] = source.[end_time_local]
              AND target.[crosses_midnight] = 0
        )
    )
        THROW 61104, 'Seed drift detected for NSE draft sessions.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
