using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.Tokens;

namespace ThesisPulse.Shared.Observability.Authentication;

public static class OperatorAuthenticationExtensions
{
    public static IServiceCollection AddThesisPulseOperatorAuthentication(
        this IServiceCollection services)
    {
        services.AddSingleton<LocalOperatorTokenService>();
        services.AddSingleton<InternalServiceTokenProvider>();
        services.AddTransient<InternalServiceAuthenticationHandler>();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, InternalServiceAuthenticationFilter>();
        services.AddHostedService<OperatorAuthenticationStartupValidator>();

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = OperatorAuthenticationConstants.Scheme;
                options.DefaultChallengeScheme = OperatorAuthenticationConstants.Scheme;
                options.DefaultForbidScheme = OperatorAuthenticationConstants.Scheme;
            })
            .AddJwtBearer(OperatorAuthenticationConstants.Scheme, _ => { });

        services
            .AddOptions<JwtBearerOptions>(OperatorAuthenticationConstants.Scheme)
            .Configure<IConfiguration, IHostEnvironment>(ConfigureJwtBearer);

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = BuildFallbackPolicy();
            options.AddPolicy(
                OperatorAuthenticationConstants.ReadPolicy,
                BuildPolicy(OperatorAuthorization.HasReadPermission));
            options.AddPolicy(
                OperatorAuthenticationConstants.OperatePolicy,
                BuildPolicy(OperatorAuthorization.HasOperatePermission));
            options.AddPolicy(
                OperatorAuthenticationConstants.AdminPolicy,
                BuildPolicy(OperatorAuthorization.HasAdminPermission));
        });

        return services;
    }

    public static IApplicationBuilder UseThesisPulseOperatorAuthentication(
        this IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    public static IEndpointRouteBuilder MapThesisPulseAuthenticationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/auth/token",
                (LocalOperatorTokenRequest request, LocalOperatorTokenService tokens) =>
                {
                    OperatorTokenResponse? token;
                    try
                    {
                        token = tokens.TryIssue(request);
                    }
                    catch (InvalidOperationException)
                    {
                        return AuthenticationProblem(
                            StatusCodes.Status503ServiceUnavailable,
                            "Authentication unavailable",
                            "Local operator sign-in is not available for this environment.");
                    }

                    return token is null
                        ? AuthenticationProblem(
                            StatusCodes.Status401Unauthorized,
                            "Authentication failed",
                            "The supplied credentials are invalid.")
                        : Results.Ok(token);
                })
            .AllowAnonymous();

        endpoints.MapGet(
            "/api/v1/auth/session",
            (ClaimsPrincipal principal) =>
            {
                var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                    ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? "unknown";
                var displayName = principal.FindFirst("name")?.Value
                    ?? principal.Identity?.Name
                    ?? subject;

                return Results.Ok(new OperatorIdentityResponse(
                    Subject: subject,
                    DisplayName: displayName,
                    Permissions: OperatorAuthorization.GetPermissions(principal)));
            });

        return endpoints;
    }

    private static AuthorizationPolicy BuildFallbackPolicy() =>
        new AuthorizationPolicyBuilder(OperatorAuthenticationConstants.Scheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(OperatorAuthorization.CanAccessRequest)
            .Build();

    private static AuthorizationPolicy BuildPolicy(
        Func<ClaimsPrincipal, bool> permissionCheck) =>
        new AuthorizationPolicyBuilder(OperatorAuthenticationConstants.Scheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context => permissionCheck(context.User))
            .Build();

    private static void ConfigureJwtBearer(
        JwtBearerOptions jwt,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var resolved = OperatorAuthenticationConfiguration.Resolve(configuration, environment);

        jwt.MapInboundClaims = false;
        jwt.RequireHttpsMetadata = resolved.RequireHttpsMetadata;
        jwt.SaveToken = false;
        jwt.IncludeErrorDetails = false;
        jwt.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await WriteProblemAsync(
                    context.Response,
                    StatusCodes.Status401Unauthorized,
                    "Authentication required",
                    "A valid ThesisPulse operator token is required.",
                    context.HttpContext.RequestAborted);
            },
            OnForbidden = context => WriteProblemAsync(
                context.Response,
                StatusCodes.Status403Forbidden,
                "Access denied",
                "The authenticated operator does not have the required permission.",
                context.HttpContext.RequestAborted),
        };

        if (string.Equals(
                resolved.Mode,
                OperatorAuthenticationConstants.LocalMode,
                StringComparison.Ordinal))
        {
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = resolved.Issuer,
                ValidateAudience = true,
                ValidAudience = resolved.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(resolved.LocalSigningKey!),
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "name",
            };
            return;
        }

        jwt.Authority = resolved.Authority;
        jwt.Audience = resolved.Audience;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudience = resolved.Audience,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "name",
        };
    }

    private static IResult AuthenticationProblem(
        int statusCode,
        string title,
        string detail) =>
        Results.Json(
            new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail,
                Type = $"https://httpstatuses.com/{statusCode}",
            },
            statusCode: statusCode,
            contentType: "application/problem+json");

    private static Task WriteProblemAsync(
        HttpResponse response,
        int statusCode,
        string title,
        string detail,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/problem+json";
        return response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail,
                Type = $"https://httpstatuses.com/{statusCode}",
            },
            cancellationToken);
    }
}
