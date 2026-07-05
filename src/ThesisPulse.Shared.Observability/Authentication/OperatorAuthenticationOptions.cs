namespace ThesisPulse.Shared.Observability.Authentication;

public sealed class OperatorAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public string Mode { get; set; } = string.Empty;

    public string Authority { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public bool RequireHttpsMetadata { get; set; } = true;

    public int TokenLifetimeMinutes { get; set; } = 30;

    public string ServiceAccessToken { get; set; } = string.Empty;

    public string[] InternalServiceHosts { get; set; } = Array.Empty<string>();

    public LocalOperatorAuthenticationOptions Local { get; set; } = new();
}

public sealed class LocalOperatorAuthenticationOptions
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string SigningKeyBase64 { get; set; } = string.Empty;

    public string[] Permissions { get; set; } = Array.Empty<string>();
}
