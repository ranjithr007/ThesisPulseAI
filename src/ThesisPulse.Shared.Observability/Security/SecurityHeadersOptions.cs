using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ThesisPulse.Shared.Observability.Security;

public sealed class SecurityHeadersOptions
{
    public const string SectionName = "SecurityHeaders";

    public bool EnableStrictTransportSecurity { get; set; }

    public int StrictTransportSecurityMaxAgeSeconds { get; set; } = 31_536_000;

    public bool IncludeSubDomains { get; set; } = true;

    public bool Preload { get; set; }

    public static SecurityHeadersOptions Resolve(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration
            .GetSection(SectionName)
            .Get<SecurityHeadersOptions>()
            ?? new SecurityHeadersOptions();

        options.Validate(configuration, environment);
        return options;
    }

    public void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (StrictTransportSecurityMaxAgeSeconds is < 300 or > 63_072_000)
        {
            throw new InvalidOperationException(
                "SecurityHeaders:StrictTransportSecurityMaxAgeSeconds must be between 300 and 63072000.");
        }

        if (!EnableStrictTransportSecurity)
        {
            return;
        }

        var platformEnvironment = configuration["Platform:Environment"] ?? "PAPER";
        if (environment.IsDevelopment() ||
            string.Equals(platformEnvironment, "PAPER", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SecurityHeaders:EnableStrictTransportSecurity cannot be enabled for Development/PAPER local HTTP environments.");
        }
    }

    public string BuildStrictTransportSecurityHeader()
    {
        var value = $"max-age={StrictTransportSecurityMaxAgeSeconds}";
        if (IncludeSubDomains)
        {
            value += "; includeSubDomains";
        }

        if (Preload)
        {
            value += "; preload";
        }

        return value;
    }
}
