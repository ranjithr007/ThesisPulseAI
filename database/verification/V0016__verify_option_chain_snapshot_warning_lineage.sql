SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[market].[option_chain_snapshots]', N'U') IS NULL
    THROW 62601, 'market.option_chain_snapshots is missing.', 1;

IF COL_LENGTH(N'[market].[option_chain_snapshots]', N'warnings_json') IS NULL
    THROW 62602, 'option-chain warnings_json column is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'[market].[option_chain_snapshots]')
      AND [name] = N'ck_option_chain_snapshots_warnings_json'
)
    THROW 62603, 'option-chain warnings JSON constraint is missing.', 1;

SELECT 'OPTION_CHAIN_SNAPSHOT_WARNING_LINEAGE_OK' AS [verification_result];
