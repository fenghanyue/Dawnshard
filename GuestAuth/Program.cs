using System.Security.Cryptography;
using GuestAuth;
using Npgsql;

if (args is ["--health-check"])
{
    try
    {
        using HttpClient healthClient = new() { Timeout = TimeSpan.FromSeconds(3) };
        using HttpResponseMessage response = await healthClient.GetAsync(
            "http://127.0.0.1:8080/health"
        );
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        Environment.ExitCode = 1;
    }
    catch (OperationCanceledException)
    {
        Environment.ExitCode = 1;
    }

    return;
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string? runtimeFile = builder.Configuration["GuestAuthRuntimeFile"];
if (runtimeFile is not null)
{
    builder.Configuration.AddJsonFile(runtimeFile, optional: false);
}

builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 65536);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton(serviceProvider =>
{
    AuthOptions configured =
        serviceProvider
            .GetRequiredService<IConfiguration>()
            .GetSection("GuestAuth")
            .Get<AuthOptions>()
        ?? new();
    if (
        string.IsNullOrWhiteSpace(configured.ConnectionString)
        || !Uri.TryCreate(configured.UpstreamUrl, UriKind.Absolute, out Uri? upstream)
        || upstream.Scheme != "https"
        || configured.TokenLifetimeSeconds is < 60 or > 86400
    )
    {
        throw new InvalidOperationException("Guest authentication configuration is invalid.");
    }

    return configured;
});
builder.Services.AddSingleton(serviceProvider =>
    NpgsqlDataSource.Create(serviceProvider.GetRequiredService<AuthOptions>().ConnectionString)
);
builder.Services.AddSingleton<GuestStore>();
builder.Services.AddSingleton<TokenService>();
builder
    .Services.AddHttpClient<UpstreamMigration>(client => client.Timeout = TimeSpan.FromSeconds(12))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

WebApplication app = builder.Build();
AuthOptions options = app.Services.GetRequiredService<AuthOptions>();
GuestStore store = app.Services.GetRequiredService<GuestStore>();
await store.InitializeAsync(CancellationToken.None);
app.Services.GetRequiredService<TokenService>()
    .Initialize(await store.HasAccountsAsync(CancellationToken.None));

app.MapGet(
    "/health",
    async (GuestStore database, CancellationToken ct) =>
    {
        await database.CheckHealthAsync(ct);
        return Results.Ok(new { status = "Healthy" });
    }
);
app.MapGet("/.well-known/jwks.json", (TokenService tokens) => tokens.PublicKeys());
app.MapGet(
    "/.well-known/openid-configuration",
    (HttpRequest request) =>
        new
        {
            issuer = options.Issuer,
            jwks_uri = $"{(request.Headers["X-Forwarded-Proto"] == "https" ? "https" : request.Scheme)}://{request.Host}/.well-known/jwks.json",
            id_token_signing_alg_values_supported = TokenService.SigningAlgorithms,
        }
);
app.MapPost(
    "/core/v1/gateway/sdk/login",
    async (
        LoginRequest request,
        HttpContext context,
        GuestStore database,
        TokenService tokens,
        UpstreamMigration migration,
        ILoggerFactory logFactory
    ) =>
    {
        try
        {
            if (
                request.DeviceAccount is null
                && (!options.AllowNewGuests || !string.IsNullOrEmpty(request.PreviousUserId))
            )
            {
                return Results.Conflict(new { error = "A preserved guest identity is required." });
            }

            bool fresh = request.DeviceAccount is null;
            DeviceCredential credential =
                request.DeviceAccount
                ?? new(
                    RandomNumberGenerator.GetString("0123456789abcdefghijklmnopqrstuvwxyz", 16),
                    RandomNumberGenerator.GetString(
                        "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
                        64
                    )
                );
            if (
                !UpstreamMigration.IsIdentifier(credential.Id)
                || string.IsNullOrEmpty(credential.Password)
                || credential.Password.Length > 256
            )
            {
                return Results.BadRequest(new { error = "Invalid guest credentials." });
            }

            DeviceRegistration registration = await database.LoginAsync(
                credential,
                ct =>
                    fresh
                        ? Task.FromResult(
                            RandomNumberGenerator.GetString(
                                "0123456789abcdefghijklmnopqrstuvwxyz",
                                16
                            )
                        )
                        : migration.VerifyAsync(request, ct),
                context.RequestAborted
            );
            if (
                !string.IsNullOrEmpty(request.PreviousUserId)
                && request.PreviousUserId != registration.Subject
            )
            {
                return Results.Conflict(
                    new { error = "Guest identity does not match the previous account." }
                );
            }

            return Results.Ok(
                new
                {
                    idToken = tokens.Create(registration.Subject, "baas-Id"),
                    accessToken = tokens.Create(registration.Subject, "baas-Access"),
                    user = new
                    {
                        id = registration.Subject,
                        nickname = "",
                        country = "",
                        birthday = "0000-00-00",
                        gender = "unknown",
                        createdAt = registration.CreatedAt,
                        updatedAt = registration.CreatedAt,
                        permissions = new
                        {
                            personalAnalytics = false,
                            personalNotification = false,
                            personalAnalyticsUpdatedAt = 0,
                            personalNotificationUpdatedAt = 0,
                        },
                        hasUnreadCsComment = false,
                        links = (object?)null,
                        deviceAccounts = new[] { new { id = credential.Id } },
                    },
                    createdDeviceAccount = fresh ? credential : null,
                    sessionId = "",
                    expiresIn = options.TokenLifetimeSeconds,
                    market = (object?)null,
                    capability = new
                    {
                        accountHost = context.Request.Host.Value,
                        accountApiHost = context.Request.Host.Value,
                        pointProgramHost = context.Request.Host.Value,
                        sessionUpdateInterval = options.TokenLifetimeSeconds,
                    },
                    behaviorSettings = new { },
                }
            );
        }
        catch (LoginFailure failure)
        {
            logFactory
                .CreateLogger("GuestLogin")
                .LogWarning("Guest login rejected with status {StatusCode}.", failure.StatusCode);
            return Results.Json(
                new
                {
                    error = "Guest authentication could not be completed; existing identity was preserved.",
                },
                statusCode: failure.StatusCode
            );
        }
    }
);
app.MapGet(
    "/bigdata/v1/analytics/events/config",
    () =>
        new
        {
            accessToken = "aaa",
            applicationId = "c6e6e04aaa8c635a",
            city = "aaa",
            country = "US",
            expirationTime = long.MaxValue,
            immediateReporting = false,
            mode = "V2",
            region = "eng",
            reportingPeriod = 2147483000,
            topic = "aaa",
        }
);
app.MapPost("/bigdata/v1/analytics/events", () => Results.Accepted());
app.MapGet(
    "/inquiry/v1/users/{userId}",
    async (string userId, HttpRequest request, TokenService tokens) =>
    {
        string header = request.Headers.Authorization.ToString();
        string? subject = header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? await tokens.ValidateAccessAsync(header[7..])
            : null;
        return subject == userId
            ? Results.Ok(
                new
                {
                    userId = subject,
                    hasUnreadCsComment = false,
                    updatedAt = 0,
                }
            )
            : Results.Unauthorized();
    }
);
app.Run();

namespace GuestAuth
{
    public partial class Program;
}
