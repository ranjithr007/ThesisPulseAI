using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ThesisPulse.Shared.Observability.Authentication;

public sealed class InternalServiceAuthenticationHandler : DelegatingHandler
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly InternalServiceTokenProvider _tokens;

    public InternalServiceAuthenticationHandler(
        IConfiguration configuration,
        IHostEnvironment environment,
        InternalServiceTokenProvider tokens)
    {
        _configuration = configuration;
        _environment = environment;
        _tokens = tokens;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is not null &&
            request.Headers.Authorization is null &&
            IsInternalHost(request.RequestUri.Host))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _tokens.GetAccessToken());
        }

        return base.SendAsync(request, cancellationToken);
    }

    private bool IsInternalHost(string host)
    {
        var resolved = OperatorAuthenticationConfiguration.Resolve(
            _configuration,
            _environment);
        return resolved.InternalServiceHosts.Contains(host);
    }
}
