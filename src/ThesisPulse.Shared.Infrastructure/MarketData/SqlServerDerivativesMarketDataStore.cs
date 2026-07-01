using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerDerivativesMarketDataStore : IDerivativesMarketDataStore
{
    private readonly SqlServerMarketDataOptions _options;
    private readonly MarketDataPublicationFactory _publicationFactory;

    public SqlServerDerivativesMarketDataStore(
        SqlServerMarketDataOptions options,
        MarketDataPublicationFactory? publicationFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _publicationFactory = publicationFactory ?? new MarketDataPublicationFactory(
            new MarketDataPublicationOptions());
    }
}
