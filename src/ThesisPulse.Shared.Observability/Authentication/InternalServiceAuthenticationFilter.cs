using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace ThesisPulse.Shared.Observability.Authentication;

public sealed class InternalServiceAuthenticationFilter : IHttpMessageHandlerBuilderFilter
{
    private readonly IServiceProvider _services;

    public InternalServiceAuthenticationFilter(IServiceProvider services)
    {
        _services = services;
    }

    public Action<HttpMessageHandlerBuilder> Configure(
        Action<HttpMessageHandlerBuilder> next)
    {
        return builder =>
        {
            next(builder);
            builder.AdditionalHandlers.Insert(
                0,
                _services.GetRequiredService<InternalServiceAuthenticationHandler>());
        };
    }
}
