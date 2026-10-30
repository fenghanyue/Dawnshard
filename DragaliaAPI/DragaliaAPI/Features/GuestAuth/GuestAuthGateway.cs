using System.Net;
using DragaliaAPI.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace DragaliaAPI.Features.GuestAuth;

public static class GuestAuthGateway
{
    public const string ClientName = "GuestAuthGateway";

    public static string SelectScheme(HttpContext context)
    {
        GuestAuthOptions options = context
            .RequestServices.GetRequiredService<IOptions<GuestAuthOptions>>()
            .Value;
        string? token = context.Request.Headers["ID-TOKEN"].FirstOrDefault();
        if (options.Enabled && token is { Length: > 0 and <= 16384 })
        {
            try
            {
                if (
                    new JsonWebTokenHandler().CanReadToken(token)
                    && new JsonWebToken(token).Issuer == options.Issuer
                )
                {
                    // Parsing chooses a verifier only. The selected handler still validates signature, issuer, audience and expiry.
                    return AuthConstants.SchemeNames.GuestJwt;
                }
            }
            catch (ArgumentException)
            {
                // Malformed input goes through the existing token-error response.
            }
        }

        return AuthConstants.SchemeNames.LegacyGameJwt;
    }

    public static WebApplication MapGuestAuthGateway(this WebApplication app)
    {
        foreach (
            (string path, string method) in new[]
            {
                ("/core/v1/gateway/sdk/login", "POST"),
                ("/.well-known/openid-configuration", "GET"),
                ("/.well-known/jwks.json", "GET"),
                ("/bigdata/v1/analytics/events/config", "GET"),
                ("/bigdata/v1/analytics/events", "POST"),
                ("/inquiry/v1/users/{userId}", "GET"),
            }
        )
        {
            app.MapMethods(path, [method], ForwardAsync);
        }

        return app;
    }

    private static async Task ForwardAsync(HttpContext context)
    {
        GuestAuthOptions options = context
            .RequestServices.GetRequiredService<IOptions<GuestAuthOptions>>()
            .Value;
        if (!options.Enabled || !options.ClientEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!context.Request.IsHttps)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        if (context.Request.ContentLength > 65536)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = 65536;
        }

        using HttpRequestMessage request = new(
            new HttpMethod(context.Request.Method),
            options.InternalUrl.TrimEnd('/') + context.Request.Path
        );
        request.Headers.Host = context.Request.Host.Value;
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
        if (
            context.Request.Headers.TryGetValue(
                "Authorization",
                out Microsoft.Extensions.Primitives.StringValues authorization
            )
        )
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization.ToArray());
        }

        if (context.Request.Method == "POST")
        {
            request.Content = new StreamContent(context.Request.Body);
            request.Content.Headers.ContentType = new("application/json");
        }

        try
        {
            HttpClient client = context
                .RequestServices.GetRequiredService<IHttpClientFactory>()
                .CreateClient(ClientName);
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted
            );
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType =
                response.Content.Headers.ContentType?.ToString() ?? "application/json";
            context.Response.Headers.CacheControl = "no-store";
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or OperationCanceledException)
        {
            if (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        }
    }
}

public sealed class GuestAuthHealthCheck(
    IOptions<GuestAuthOptions> options,
    IHttpClientFactory clients
) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (!options.Value.Enabled)
        {
            return HealthCheckResult.Healthy("Guest authentication is disabled.");
        }

        try
        {
            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            using HttpResponseMessage response = await clients
                .CreateClient(GuestAuthGateway.ClientName)
                .GetAsync(options.Value.InternalUrl.TrimEnd('/') + "/health", deadline.Token);
            return response.StatusCode == HttpStatusCode.OK
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Guest authentication is unavailable.");
        }
        catch (Exception exception)
            when (exception is HttpRequestException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Guest authentication is unavailable.");
        }
    }
}
