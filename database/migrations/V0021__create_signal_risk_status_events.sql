SET XACT_ABORT ON;
GO
BEGIN TRANSACTION;
CREATE TABLE [risk].[signal_risk_status_events]
(
    [signal_risk_status_event_id] bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [transition_uid] uniqueidentifier NOT NULL UNIQUE,
    [signal_risk_evaluation_id] bigint NOT NULL,
    [event_sequence] int NOT NULL,
    [from_status] varchar(30) NOT NULL,
    [to_status] varchar(30) NOT NULL,
    [occurred_at_utc] datetime2(7) NOT NULL,
    [correlation_id] uniqueidentifier NOT NULL,
    [causation_id] uniqueidentifier NULL,
    CONSTRAINT [uq_signal_risk_event_sequence] UNIQUE ([signal_risk_evaluation_id], [event_sequence]),
    CONSTRAINT [fk_signal_risk_event_evaluation] FOREIGN KEY ([signal_risk_evaluation_id]) REFERENCES [risk].[signal_risk_evaluations] ([signal_risk_evaluation_id]),
    CONSTRAINT [ck_signal_risk_event_sequence] CHECK ([event_sequence] >= 0)
);
COMMIT TRANSACTION;
GO
