SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[reference].[derivative_contracts]', N'U') IS NULL
    THROW 62501, 'reference.derivative_contracts is missing.', 1;

IF OBJECT_ID(N'[reference].[derivative_expiry_schedules]', N'U') IS NULL
    THROW 62502, 'reference.derivative_expiry_schedules is missing.', 1;

IF OBJECT_ID(N'[market].[futures_basis_observations]', N'U') IS NULL
    THROW 62503, 'market.futures_basis_observations is missing.', 1;

IF OBJECT_ID(N'[market].[option_chain_snapshots]', N'U') IS NULL
    THROW 62504, 'market.option_chain_snapshots is missing.', 1;

IF OBJECT_ID(N'[market].[option_chain_entries]', N'U') IS NULL
    THROW 62505, 'market.option_chain_entries is missing.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[reference].[derivative_contracts]')
      AND [name] = N'ux_derivative_contracts_current_instrument'
      AND [is_unique] = 1
)
    THROW 62506, 'Current derivative-contract uniqueness index is missing.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[market].[option_chain_snapshots]')
      AND [name] = N'ix_option_chain_snapshots_point_in_time'
)
    THROW 62507, 'Option-chain point-in-time index is missing.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE [parent_object_id] = OBJECT_ID(N'[market].[option_chain_entries]')
      AND [name] = N'fk_option_chain_entries_contract'
)
    THROW 62508, 'Option-chain contract foreign key is missing.', 1;

SELECT 'DERIVATIVES_REFERENCE_AND_MARKET_DATA_TABLES_OK' AS [verification_result];
