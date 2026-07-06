using Microsoft.Extensions.Configuration;

namespace ThesisPulse.Shared.Observability.Security;

public static class CorsOriginValidator
{
    public const string SectionName = "Cors:AllowedOrigins";
    public const string DefaultLocalFrontendOrigin = "http://localhost:5173";

    public static string[] ResolveAllowedOrigins(IConfiguration configuration)
    {
        var configured = configuration
            .GetSection(SectionName)
            .GetChildren()
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();

        var origins = configured.Length == 0
            ? new[] { DefaultLocalFrontendOrigin }
            : configured;

        Validate(origins);
        return origins;
    }

    public static void Validate(IEnumerable<string> origins)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var origin in origins)
        {
            index++;
            ValidateOne(origin, index);
            if (!seen.Add(origin.TrimEnd('/')))
            {
                throw new InvalidOperationException(
                    $"Cors:AllowedOrigins contains duplicate origin '{origin}'.");
            }
        }

        if (index == 0)
        {
            throw new InvalidOperationException(
                "Cors:AllowedOrigins must contain at least one explicit origin.");
        }
    }

    private static void ValidateOne(string origin, int index)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            throw new InvalidOperationException(
                $"Cors:AllowedOrigins:{index - 1} cannot be empty.");
        }

        if (origin.Contains('*', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cors:AllowedOrigins:{index - 1} cannot contain wildcard origins.");
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Cors:AllowedOrigins:{index - 1} must be an absolute origin URI.");
        }

        if (uri.Scheme is not "http" and not "https")
        {
            throw new InvalidOperationException(
                $"Cors:AllowedOrigins:{index - 1} must use http or https.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                $"Cors:AllowedOrigins:{index - 1} cannot contain user information.");
        }

        if (!string.IsNullOrWhiteSpace(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException(
                $"Cors:AllowedOrigins:{index - 1} must not contain a path.");
        }

        if (!string.IsNullOrWhiteSpace(uri.Query) || !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"Cors:AllowedOrigins:{index - 1} must not contain query strings or fragments.");
        }
    }
}
