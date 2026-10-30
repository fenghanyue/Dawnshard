using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using DragaliaAPI.Features.GuestAuth;
using DragaliaAPI.Features.Shared.Options;
using DragaliaAPI.Infrastructure.Authentication;
using DragaliaAPI.Infrastructure.Results;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DragaliaAPI.Integration.Test.Features.Login;

public sealed class GuestAuthTest(
    CustomWebApplicationFactory factory,
    ITestOutputHelper outputHelper
) : TestFixture(factory, outputHelper)
{
    private HttpClient GuestClient(
        SecurityKey localKey,
        HttpMessageHandler? gateway = null,
        string? sourceAddress = null,
        string? trustedAddress = null
    )
    {
        return this.CreateClient(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<GuestAuthOptions>(options =>
                {
                    options.Enabled = true;
                    options.ClientEnabled = true;
                    if (trustedAddress is not null)
                    {
                        options.TrustedProxyAddresses.Add(trustedAddress);
                    }
                });
                services.Configure<DragalipatchOptions>(options => options.UseUnifiedLogin = false);
                services.PostConfigure<JwtBearerOptions>(
                    AuthConstants.SchemeNames.GuestJwt,
                    options =>
                    {
                        options.Authority = null;
                        options.ConfigurationManager = null;
                        options.TokenValidationParameters = new()
                        {
                            ValidIssuer = "DawnshardGuest",
                            ValidAudience = "baas-Id",
                            IssuerSigningKey = localKey,
                            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                        };
                    }
                );
                if (gateway is not null)
                {
                    services
                        .AddHttpClient(GuestAuthGateway.ClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => gateway);
                }

                if (sourceAddress is not null)
                {
                    services.AddSingleton<IStartupFilter>(new SourceAddressFilter(sourceAddress));
                }
            })
        );
    }

    private static string LocalToken(
        SecurityKey key,
        string subject,
        string audience = "baas-Id"
    ) =>
        new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = "DawnshardGuest",
                Audience = audience,
                IssuedAt = DateTime.UtcNow,
                Expires = DateTime.UtcNow.AddMinutes(5),
                Claims = new Dictionary<string, object> { ["sub"] = subject },
                SigningCredentials = new(key, SecurityAlgorithms.RsaSha256),
            }
        );

    [Fact]
    public async Task MigratedGuest_LogsIntoOriginalSave_WithoutCreatingAnotherPlayer()
    {
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey key = new(rsa) { KeyId = "local" };
        using HttpClient client = this.GuestClient(key);
        int before = await this
            .ApiContext.Players.IgnoreQueryFilters()
            .Where(player => player.AccountId == DeviceAccountId)
            .CountAsync(TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Add("ID-TOKEN", LocalToken(key, DeviceAccountId));
        ToolAuthResponse response = (
            await client.PostMsgpack<ToolAuthResponse>(
                "/tool/auth",
                cancellationToken: TestContext.Current.CancellationToken
            )
        ).Data;
        response.ViewerId.Should().Be((ulong)this.ViewerId);
        (
            await this
                .ApiContext.Players.IgnoreQueryFilters()
                .Where(player => player.AccountId == DeviceAccountId)
                .CountAsync(TestContext.Current.CancellationToken)
        )
            .Should()
            .Be(before);
    }

    [Theory]
    [InlineData("legacy-key")]
    [InlineData("wrong-audience")]
    public async Task LocalIssuer_CannotUseLegacyKeyOrWrongAudience(string mode)
    {
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey local = new(rsa) { KeyId = "local" };
        using HttpClient client = this.GuestClient(local);
        SecurityKey signer = mode == "legacy-key" ? TokenHelper.SecurityKeys[0] : local;
        client.DefaultRequestHeaders.Add(
            "ID-TOKEN",
            LocalToken(
                signer,
                DeviceAccountId,
                mode == "wrong-audience" ? "baas-Access" : "baas-Id"
            )
        );
        DragaliaResponse<ResultCodeResponse> response =
            await client.PostMsgpack<ResultCodeResponse>(
                "/tool/auth",
                ensureSuccessHeader: false,
                cancellationToken: TestContext.Current.CancellationToken
            );
        response.Data.ResultCode.Should().Be(ResultCode.IdTokenError);
    }

    [Fact]
    public async Task LegacyClient_RemainsUsableDuringMigration()
    {
        using RSA rsa = RSA.Create(2048);
        using HttpClient client = this.GuestClient(new RsaSecurityKey(rsa) { KeyId = "local" });
        client.DefaultRequestHeaders.Add(
            "ID-TOKEN",
            TokenHelper.GetToken(DeviceAccountId, DateTimeOffset.UtcNow.AddMinutes(5))
        );
        ToolAuthResponse response = (
            await client.PostMsgpack<ToolAuthResponse>(
                "/tool/auth",
                cancellationToken: TestContext.Current.CancellationToken
            )
        ).Data;
        response.ViewerId.Should().Be((ulong)this.ViewerId);
    }

    [Fact]
    public async Task PlainHttp_CannotSubmitLongLivedGuestCredentials()
    {
        using RSA rsa = RSA.Create(2048);
        using HttpClient client = this.GuestClient(new RsaSecurityKey(rsa));
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "http://localhost/core/v1/gateway/sdk/login"
        )
        {
            Content = new StringContent("{}"),
        };
        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be((HttpStatusCode)426);
    }

    [Fact]
    public async Task HttpsGateway_ForwardsOnlyTheRequiredHeaders_AndPreventsCaching()
    {
        using RSA rsa = RSA.Create(2048);
        RelayProbe probe = new();
        using HttpClient client = this.GuestClient(new RsaSecurityKey(rsa), probe);
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "https://auth.example/core/v1/gateway/sdk/login"
        )
        {
            Content = new StringContent("{}"),
        };
        request.Headers.Add("Cookie", "private-cookie");
        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        probe.Seen.Should().BeTrue();
    }

    [Theory]
    [InlineData("203.0.113.8", null, 426)]
    [InlineData("172.20.0.2", "172.20.0.2", 200)]
    public async Task ForwardedHttps_IsAcceptedOnlyFromConfiguredProxy(
        string source,
        string? trusted,
        int expected
    )
    {
        using RSA rsa = RSA.Create(2048);
        RelayProbe probe = new();
        using HttpClient client = this.GuestClient(new RsaSecurityKey(rsa), probe, source, trusted);
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "http://auth.example/core/v1/gateway/sdk/login"
        )
        {
            Content = new StringContent("{}"),
        };
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "192.0.2.9");
        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be((HttpStatusCode)expected);
        probe.Seen.Should().Be(expected == 200);
    }

    [Theory]
    [InlineData("https", false)]
    [InlineData("http", true)]
    public async Task PatchConfiguration_AdvertisesLocalAuthenticationOnlyOverHttps(
        string scheme,
        bool unified
    )
    {
        using RSA rsa = RSA.Create(2048);
        using HttpClient client = this.GuestClient(new RsaSecurityKey(rsa));
        using JsonDocument body = (
            await client.GetFromJsonAsync<JsonDocument>(
                scheme + "://auth.example/dragalipatch/config",
                TestContext.Current.CancellationToken
            )
        )!;
        body.RootElement.GetProperty("useUnifiedLogin").GetBoolean().Should().Be(unified);
    }

    private sealed class SourceAddressFilter(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(nextMiddleware =>
                    context =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
                        return nextMiddleware(context);
                    }
                );
                next(app);
            };
    }

    private sealed class RelayProbe : HttpMessageHandler
    {
        public bool Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            request.Headers.Host.Should().Be("auth.example");
            request.Headers.Contains("Cookie").Should().BeFalse();
            request
                .Headers.GetValues("X-Forwarded-Proto")
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be("https");
            this.Seen = true;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }
            );
        }
    }
}
