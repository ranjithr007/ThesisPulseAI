using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerDerivativesMarketDataStore : IDerivativesMarketDataStore
{
    private readonly SqlServerMarketDataOptions _options;

    public SqlServerDerivativesMarketDataStore(SqlServerMarketDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }
}
