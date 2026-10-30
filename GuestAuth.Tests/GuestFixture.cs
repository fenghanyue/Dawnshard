using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GuestAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace GuestAuth.Tests;

[CollectionDefinition("Guest authentication", DisableParallelization = true)]
public sealed class GuestCollection : ICollectionFixture<GuestFixture>;

public sealed class GuestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17").Build();
    public readonly FakeUpstream Upstream = new();
    public readonly CapturedLogs Logs = new();
    public string DataDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "dawnshard-guest-tests-" + Guid.NewGuid());
    public WebApplicationFactory<GuestAuth.Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await this.postgres.StartAsync();
        this.Factory = new WebApplicationFactory<GuestAuth.Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(
                (_, config) =>
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["GuestAuth:ConnectionString"] = this.postgres.GetConnectionString(),
                            ["GuestAuth:DataDirectory"] = this.DataDirectory,
                            ["GuestAuth:UpstreamUrl"] = "https://upstream.example",
                            ["GuestAuth:AllowNewGuests"] = "true",
                        }
                    )
            );
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddHttpClient<UpstreamMigration>()
                    .ConfigurePrimaryHttpMessageHandler(() => this.Upstream);
                services.AddSingleton<ILoggerProvider>(this.Logs);
            });
        });
        this.Client = this.Factory.CreateClient();
        using HttpResponseMessage health = await this.Client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
    }

    public async Task<long> CountAsync(string id)
    {
        await using NpgsqlConnection connection = new(this.postgres.GetConnectionString());
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "SELECT COUNT(*) FROM guest_devices WHERE id=$1",
            connection
        );
        command.Parameters.AddWithValue(id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        await this.Factory.DisposeAsync();
        await this.postgres.DisposeAsync();
        if (Directory.Exists(this.DataDirectory))
        {
            Directory.Delete(this.DataDirectory, true);
        }
        this.Upstream.Dispose();
    }
}

public sealed class FakeUpstream : HttpMessageHandler
{
    private readonly RSA rsa = RSA.Create(2048);
    public string Mode { get; set; } = "valid";
    public string Subject { get; set; } = "oldguest00000001";
    public int LoginCalls;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (request.Method == HttpMethod.Post)
        {
            Interlocked.Increment(ref this.LoginCalls);
            LoginRequest login = (
                await request.Content!.ReadFromJsonAsync<LoginRequest>(cancellationToken)
            )!;
            if (this.Mode == "unavailable")
            {
                return new(HttpStatusCode.ServiceUnavailable);
            }
            if (login.DeviceAccount!.Password != "original-password")
            {
                return new(HttpStatusCode.BadRequest);
            }
            DateTime now = DateTime.UtcNow;
            using RSA forged = RSA.Create(2048);
            string token = new JsonWebTokenHandler().CreateToken(
                new SecurityTokenDescriptor
                {
                    Issuer = this.Mode == "issuer" ? "attacker" : "LukeFZ",
                    Audience = this.Mode == "audience" ? "wrong" : "baas-Id",
                    IssuedAt = now.AddHours(-1),
                    NotBefore = now.AddHours(-1),
                    Expires = this.Mode == "expired" ? now.AddMinutes(-5) : now.AddMinutes(5),
                    Claims = new Dictionary<string, object> { ["sub"] = this.Subject },
                    SigningCredentials = new(
                        new RsaSecurityKey(this.Mode == "forged" ? forged : this.rsa)
                        {
                            KeyId = "upstream",
                        },
                        SecurityAlgorithms.RsaSha256
                    ),
                }
            );
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new
                    {
                        idToken = token,
                        user = new
                        {
                            id = this.Mode == "user-mismatch" ? "anotherguest0001" : this.Subject,
                        },
                    }
                ),
            };
        }
        if (
            request.RequestUri!.AbsolutePath.EndsWith(
                "openid-configuration",
                StringComparison.Ordinal
            )
        )
        {
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new
                    {
                        issuer = "LukeFZ",
                        jwks_uri = "https://upstream.example/.well-known/jwks.json",
                    }
                ),
            };
        }
        RSAParameters parameters = this.rsa.ExportParameters(false);
        return new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                new
                {
                    keys = new[]
                    {
                        new
                        {
                            kty = "RSA",
                            kid = "upstream",
                            alg = "RS256",
                            use = "sig",
                            n = Base64UrlEncoder.Encode(parameters.Modulus!),
                            e = Base64UrlEncoder.Encode(parameters.Exponent!),
                        },
                    },
                }
            ),
        };
    }
}

public sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Capture(this.Messages);

    public void Dispose() { }

    private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => messages.Enqueue(formatter(state, exception));
    }
}
