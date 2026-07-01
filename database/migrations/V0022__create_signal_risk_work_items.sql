SET XACT_ABORT ON;
GO
BEGIN TRANSACTION;
CREATE TABLE [risk].[signal_risk_work_items]
(
    [signal_risk_work_item_id] bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [message_uid] uniqueidentifier NOT NULL UNIQUE,
    [signal_uid] uniqueidentifier NOT NULL,
    [intake_json] nvarchar(max) NOT NULL,
    [status] varchar(30) NOT NULL,
    [attempt_count] int NOT NULL DEFAULT 0,
    [available_at_utc] datetime2(7) NOT NULL,
    [lease_owner] varchar(200) NULL,
    [lease_expires_at_utc] datetime2(7) NULL,
    [last_error] nvarchar(2000) NULL,
    [created_at_utc] datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    [updated_at_utc] datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT [ck_signal_risk_work_json] CHECK (ISJSON([intake_json]) = 1),
    CONSTRAINT [ck_signal_risk_work_attempts] CHECK ([attempt_count] >= 0),
    CONSTRAINT [ck_signal_risk_work_status] CHECK ([status] IN ('PENDING','LEASED','COMPLETED','RETRY_PENDING','EXPIRED','FAILED'))
);
CREATE INDEX [ix_signal_risk_work_ready]
    ON [risk].[signal_risk_work_items] ([status], [available_at_utc], [lease_expires_at_utc])
    INCLUDE ([attempt_count], [signal_uid]);
COMMIT TRANSACTION;
GO
