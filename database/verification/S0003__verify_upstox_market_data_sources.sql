SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[reference].[brokers]', N'U') IS NULL
    THROW 62701, 'reference.brokers does not exist.', 1;

IF OBJECT_ID(N'[market].[data_sources]', N'U') IS NULL
    THROW 62702, 'market.data_sources does not exist.', 1;

IF
(
    SELECT COUNT_BIG(*)
    FROM [reference].[brokers]
    WHERE [broker_code] = 'UPSTOX'
      AND [is_active] = 1
) <> 1
    THROW 62703, 'Active Upstox broker identity is invalid.', 1;

IF
(
    SELECT COUNT_BIG(*)
    FROM [market].[data_sources]
    WHERE [source_code] IN ('UPSTOX_REST', 'UPSTOX_WS')
      AND [source_type] = 'BROKER'
      AND [is_authoritative] = 1
      AND [is_active] = 1
) <> 2
    THROW 62704, 'Upstox market data source identities are invalid.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [market].[data_sources]
    WHERE [source_code] = 'UPSTOX_REST'
      AND [transport_type] = 'REST'
)
    THROW 62705, 'UPSTOX_REST transport is invalid.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [market].[data_sources]
    WHERE [source_code] = 'UPSTOX_WS'
      AND [transport_type] = 'WEBSOCKET'
)
    THROW 62706, 'UPSTOX_WS transport is invalid.', 1;

PRINT 'Upstox market data seed verification passed.';
