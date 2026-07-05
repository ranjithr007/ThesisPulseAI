using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ThesisPulse.Shared.Observability.Authentication;

public sealed class OperatorAuthenticationStartupValidator : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public OperatorAuthenticationStartupValidator(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = OperatorAuthenticationConfiguration.Resolve(_configuration, _environment);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
