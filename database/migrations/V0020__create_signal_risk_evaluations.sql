SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

CREATE TABLE [risk].[signal_risk_evaluations]
(
    [signal_risk_evaluation_id] bigint IDENTITY(1,1) NOT NULL,
    [command_uid] uniqueidentifier NOT NULL,
    [request_uid] uniqueidentifier NOT NULL,
    [source_message_uid] uniqueidentifier NOT NULL,
    [signal_id] bigint NOT NULL,
    [risk_decision_id] bigint NULL,
    [contract_version] varchar(20) NOT NULL,
    [risk_policy_version] varchar(100) NOT NULL,
    [current_status] varchar(30) NOT NULL,
    [attempt_count] int NOT NULL CONSTRAINT [df_signal_risk_attempt_count] DEFAULT 0,
    [correlation_id] uniqueidentifier NOT NULL,
    [causation_id] uniqueidentifier NULL,
    [created_at_utc] datetime2(7) NOT NULL CONSTRAINT [df_signal_risk_created_at] DEFAULT SYSUTCDATETIME(),
    [updated_at_utc] datetime2(7) NOT NULL CONSTRAINT [df_signal_risk_updated_at] DEFAULT SYSUTCDATETIME(),
    CONSTRAINT [pk_signal_risk_evaluations] PRIMARY KEY ([signal_risk_evaluation_id]),
    CONSTRAINT [uq_signal_risk_command_uid] UNIQUE ([command_uid]),
    CONSTRAINT [uq_signal_risk_request_uid] UNIQUE ([request_uid]),
    CONSTRAINT [fk_signal_risk_signal] FOREIGN KEY ([signal_id])
        REFERENCES [intelligence].[signals] ([signal_id]),
    CONSTRAINT [fk_signal_risk_decision] FOREIGN KEY ([risk_decision_id])
        REFERENCES [risk].[risk_decisions] ([risk_decision_id]),
    CONSTRAINT [ck_signal_risk_contract] CHECK ([contract_version] = '1.0.0'),
    CONSTRAINT [ck_signal_risk_attempt] CHECK ([attempt_count] >= 0),
    CONSTRAINT [ck_signal_risk_status] CHECK ([current_status] IN
        ('RISK_EVALUATING','RISK_APPROVED','RISK_REJECTED','RISK_RESTRICTED','RISK_RETRY_PENDING','RISK_EXPIRED'))
);

CREATE INDEX [ix_signal_risk_status_updated]
    ON [risk].[signal_risk_evaluations] ([current_status], [updated_at_utc]);

COMMIT TRANSACTION;
GO
