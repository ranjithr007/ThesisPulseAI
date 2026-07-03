IF OBJECT_ID('intelligence.option_chain_rollout_audit', 'U') IS NULL
BEGIN
    CREATE TABLE intelligence.option_chain_rollout_audit
    (
        audit_uid UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        correlation_uid UNIQUEIDENTIFIER NOT NULL,
        command_key NVARCHAR(128) NOT NULL,
        actor NVARCHAR(128) NOT NULL,
        action_code NVARCHAR(32) NOT NULL,
        previous_mode NVARCHAR(32) NOT NULL,
        new_mode NVARCHAR(32) NOT NULL,
        previous_version BIGINT NOT NULL,
        new_version BIGINT NOT NULL,
        reason NVARCHAR(512) NOT NULL,
        source_service NVARCHAR(128) NOT NULL,
        observed_at_utc DATETIME2(7) NOT NULL,
        selection_authority BIT NOT NULL CONSTRAINT DF_rollout_audit_selection DEFAULT (0),
        execution_authority BIT NOT NULL CONSTRAINT DF_rollout_audit_execution DEFAULT (0),
        CONSTRAINT UQ_rollout_audit_command UNIQUE (command_key),
        CONSTRAINT UQ_rollout_audit_version UNIQUE (new_version),
        CONSTRAINT CK_rollout_audit_version CHECK (new_version = previous_version + 1),
        CONSTRAINT CK_rollout_audit_authority CHECK (selection_authority = 0 AND execution_authority = 0)
    );
END;
GO

IF OBJECT_ID('intelligence.option_chain_scheduler_leases', 'U') IS NULL
BEGIN
    CREATE TABLE intelligence.option_chain_scheduler_leases
    (
        job_name NVARCHAR(64) NOT NULL PRIMARY KEY,
        owner_instance NVARCHAR(128) NOT NULL,
        lease_uid UNIQUEIDENTIFIER NOT NULL UNIQUE,
        acquired_at_utc DATETIME2(7) NOT NULL,
        expires_at_utc DATETIME2(7) NOT NULL,
        heartbeat_at_utc DATETIME2(7) NOT NULL
    );
END;
GO

IF OBJECT_ID('intelligence.option_chain_scheduler_runs', 'U') IS NULL
BEGIN
    CREATE TABLE intelligence.option_chain_scheduler_runs
    (
        run_uid UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        job_name NVARCHAR(64) NOT NULL,
        owner_instance NVARCHAR(128) NOT NULL,
        outcome NVARCHAR(32) NOT NULL,
        started_at_utc DATETIME2(7) NOT NULL,
        completed_at_utc DATETIME2(7) NULL,
        detail NVARCHAR(1024) NULL,
        selection_authority BIT NOT NULL CONSTRAINT DF_scheduler_runs_selection DEFAULT (0),
        execution_authority BIT NOT NULL CONSTRAINT DF_scheduler_runs_execution DEFAULT (0),
        CONSTRAINT CK_scheduler_runs_authority CHECK (selection_authority = 0 AND execution_authority = 0)
    );

    CREATE INDEX IX_option_chain_scheduler_runs_job_started
        ON intelligence.option_chain_scheduler_runs(job_name, started_at_utc DESC);
END;
GO
