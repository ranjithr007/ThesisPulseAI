IF OBJECT_ID(N'intelligence.option_chain_work_queue', N'U') IS NULL
BEGIN
    CREATE TABLE intelligence.option_chain_work_queue
    (
        work_uid UNIQUEIDENTIFIER NOT NULL,
        snapshot_uid UNIQUEIDENTIFIER NOT NULL,
        instrument_key NVARCHAR(128) NOT NULL,
        workflow_cutoff_utc DATETIME2(7) NOT NULL,
        engine_version NVARCHAR(64) NOT NULL,
        policy_version NVARCHAR(64) NOT NULL,
        status NVARCHAR(16) NOT NULL,
        attempt_count INT NOT NULL CONSTRAINT DF_option_chain_work_attempt_count DEFAULT (0),
        available_at_utc DATETIME2(7) NOT NULL,
        lease_owner NVARCHAR(128) NULL,
        lease_expires_at_utc DATETIME2(7) NULL,
        terminal_reason NVARCHAR(512) NULL,
        created_at_utc DATETIME2(7) NOT NULL,
        updated_at_utc DATETIME2(7) NOT NULL,
        row_version ROWVERSION NOT NULL,
        CONSTRAINT PK_option_chain_work_queue PRIMARY KEY CLUSTERED (work_uid),
        CONSTRAINT CK_option_chain_work_queue_status CHECK
            (status IN (N'PENDING', N'LEASED', N'COMPLETED', N'DUPLICATE', N'REJECTED', N'FAILED')),
        CONSTRAINT CK_option_chain_work_queue_attempt_count CHECK (attempt_count >= 0),
        CONSTRAINT CK_option_chain_work_queue_lease CHECK
            ((status = N'LEASED' AND lease_owner IS NOT NULL AND lease_expires_at_utc IS NOT NULL)
             OR (status <> N'LEASED' AND lease_owner IS NULL AND lease_expires_at_utc IS NULL))
    );

    CREATE UNIQUE INDEX UX_option_chain_work_queue_snapshot_policy
        ON intelligence.option_chain_work_queue
        (snapshot_uid, engine_version, policy_version, workflow_cutoff_utc);

    CREATE INDEX IX_option_chain_work_queue_lease
        ON intelligence.option_chain_work_queue
        (status, available_at_utc, lease_expires_at_utc, created_at_utc)
        INCLUDE (attempt_count, instrument_key, snapshot_uid);
END;
