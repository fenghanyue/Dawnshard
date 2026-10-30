using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuestAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace GuestAuth.Tests;

[Collection("Guest authentication")]
public sealed class GuestLoginTests(GuestFixture fixture)
{
    private static LoginRequest Existing(
        string id,
        string? previous = null,
        string password = "original-password"
    ) => new() { DeviceAccount = new(id, password), PreviousUserId = previous };

    private static string DeviceId() => Guid.NewGuid().ToString("N")[..16];

    private Task<HttpResponseMessage> LoginAsync(LoginRequest request) =>
        fixture.Client.PostAsJsonAsync("/core/v1/gateway/sdk/login", request);

    [Fact]
    public async Task Migration_PreservesVerifiedSubject_AndLaterLoginWorksWithoutUpstream()
    {
        fixture.Upstream.Mode = "valid";
        string id = DeviceId();
        int before = fixture.Upstream.LoginCalls;
        using HttpResponseMessage first = await this.LoginAsync(
            Existing(id, fixture.Upstream.Subject)
        );
        first.EnsureSuccessStatusCode();
        using JsonDocument body =
            await first.Content.ReadFromJsonAsync<JsonDocument>(
                TestContext.Current.CancellationToken
            ) ?? throw new Exception();
        Assert.Equal(
            fixture.Upstream.Subject,
            body.RootElement.GetProperty("user").GetProperty("id").GetString()
        );
        Assert.Equal(
            JsonValueKind.Null,
            body.RootElement.GetProperty("createdDeviceAccount").ValueKind
        );
        string token = body.RootElement.GetProperty("idToken").GetString()!;
        JsonWebToken jwt = new(token);
        Assert.Equal("DawnshardGuest", jwt.Issuer);
        Assert.Equal(fixture.Upstream.Subject, jwt.Subject);
        Assert.False(jwt.GetClaim("sav:a").Value == "true");
        fixture.Upstream.Mode = "unavailable";
        using HttpResponseMessage second = await this.LoginAsync(
            Existing(id, fixture.Upstream.Subject)
        );
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(before + 1, fixture.Upstream.LoginCalls);
        Assert.Equal(1, await fixture.CountAsync(id));
    }

    [Theory]
    [InlineData('!', false)]
    [InlineData('$', false)]
    [InlineData('+', false)]
    [InlineData('-', false)]
    [InlineData('!', true)]
    [InlineData('$', true)]
    [InlineData('+', true)]
    [InlineData('-', true)]
    public async Task Migration_AcceptsOriginalIdentifierAlphabet(char character, bool device)
    {
        // The original service generates device and subject IDs with RandomExtensions.NextString.
        string originalSubject = fixture.Upstream.Subject;
        string id = DeviceId();
        if (device)
        {
            id = character + id[1..];
        }
        else
        {
            fixture.Upstream.Subject = character + originalSubject[1..];
        }

        try
        {
            fixture.Upstream.Mode = "valid";
            string expectedSubject = fixture.Upstream.Subject;
            int before = fixture.Upstream.LoginCalls;
            using HttpResponseMessage first = await this.LoginAsync(Existing(id, expectedSubject));
            first.EnsureSuccessStatusCode();
            using JsonDocument body = (
                await first.Content.ReadFromJsonAsync<JsonDocument>(
                    TestContext.Current.CancellationToken
                )
            )!;
            Assert.Equal(
                expectedSubject,
                body.RootElement.GetProperty("user").GetProperty("id").GetString()
            );
            Assert.Equal(
                expectedSubject,
                new JsonWebToken(body.RootElement.GetProperty("idToken").GetString()!).Subject
            );
            Assert.Equal(
                JsonValueKind.Null,
                body.RootElement.GetProperty("createdDeviceAccount").ValueKind
            );

            fixture.Upstream.Mode = "unavailable";
            using HttpResponseMessage second = await this.LoginAsync(Existing(id, expectedSubject));
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(before + 1, fixture.Upstream.LoginCalls);
            Assert.Equal(1, await fixture.CountAsync(id));
        }
        finally
        {
            fixture.Upstream.Subject = originalSubject;
            fixture.Upstream.Mode = "valid";
        }
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("forged")]
    [InlineData("expired")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("user-mismatch")]
    public async Task FailedMigration_NeverRegistersOrReplacesIdentity(string mode)
    {
        fixture.Upstream.Mode = mode;
        string id = DeviceId();
        using HttpResponseMessage response = await this.LoginAsync(Existing(id));
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(0, await fixture.CountAsync(id));
        string body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken
        );
        Assert.DoesNotContain("original-password", body);
        Assert.DoesNotContain(id, body);
    }

    [Fact]
    public async Task PreviousIdAlone_CannotClaimAnExistingSave()
    {
        using HttpResponseMessage missingCredential = await this.LoginAsync(
            new() { PreviousUserId = fixture.Upstream.Subject }
        );
        Assert.Equal(HttpStatusCode.Conflict, missingCredential.StatusCode);
        fixture.Upstream.Mode = "valid";
        string id = DeviceId();
        using HttpResponseMessage wrongSubject = await this.LoginAsync(
            Existing(id, "anotherguest0001")
        );
        Assert.Equal(HttpStatusCode.Conflict, wrongSubject.StatusCode);
        Assert.Equal(0, await fixture.CountAsync(id));
    }

    [Fact]
    public async Task WrongPassword_DoesNotFallBackToUpstreamOrOverwriteIdentity()
    {
        fixture.Upstream.Mode = "valid";
        string id = DeviceId();
        using HttpResponseMessage first = await this.LoginAsync(Existing(id));
        first.EnsureSuccessStatusCode();
        int calls = fixture.Upstream.LoginCalls;
        using HttpResponseMessage wrong = await this.LoginAsync(
            Existing(id, password: "wrong-secret")
        );
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(calls, fixture.Upstream.LoginCalls);
        using HttpResponseMessage correct = await this.LoginAsync(Existing(id));
        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
    }

    [Fact]
    public async Task ConcurrentFirstLogins_EnrollExactlyOnce()
    {
        fixture.Upstream.Mode = "valid";
        string id = DeviceId();
        int calls = fixture.Upstream.LoginCalls;
        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => this.LoginAsync(Existing(id)))
        );
        foreach (HttpResponseMessage response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
        Assert.Equal(calls + 1, fixture.Upstream.LoginCalls);
        Assert.Equal(1, await fixture.CountAsync(id));
    }

    [Fact]
    public async Task FreshGuest_DoesNotContactUpstream_AndGetsReusableCredentials()
    {
        fixture.Upstream.Mode = "unavailable";
        int calls = fixture.Upstream.LoginCalls;
        using HttpResponseMessage first = await this.LoginAsync(new());
        first.EnsureSuccessStatusCode();
        using JsonDocument body = (
            await first.Content.ReadFromJsonAsync<JsonDocument>(
                TestContext.Current.CancellationToken
            )
        )!;
        DeviceCredential credential = body
            .RootElement.GetProperty("createdDeviceAccount")
            .Deserialize<DeviceCredential>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        string subject = body.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        using HttpResponseMessage second = await this.LoginAsync(
            new() { DeviceAccount = credential, PreviousUserId = subject }
        );
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(calls, fixture.Upstream.LoginCalls);
    }

    [Fact]
    public async Task SigningKey_SurvivesServiceRestart_AndMissingKeyIsNotSilentlyReplaced()
    {
        AuthOptions options = fixture.Factory.Services.GetRequiredService<AuthOptions>();
        TokenService active = fixture.Factory.Services.GetRequiredService<TokenService>();
        string token = active.Create("oldguest00000001", "baas-Access");
        using TokenService restarted = new(options);
        restarted.Initialize(hasAccounts: true);
        Assert.Equal("oldguest00000001", await restarted.ValidateAccessAsync(token));
        string empty = Path.Combine(Path.GetTempPath(), "guest-key-test-" + Guid.NewGuid());
        try
        {
            using TokenService missing = new(new() { DataDirectory = empty });
            Assert.Throws<InvalidOperationException>(() => missing.Initialize(hasAccounts: true));
            Assert.False(File.Exists(Path.Combine(empty, "signing-key.pem")));
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }

    [Fact]
    public async Task AccessToken_IsScoped_AndPrivateKeyIsNeverPublished()
    {
        TokenService tokens = fixture.Factory.Services.GetRequiredService<TokenService>();
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            "/inquiry/v1/users/oldguest00000001"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            tokens.Create("oldguest00000001", "baas-Id")
        );
        using HttpResponseMessage rejected = await fixture.Client.SendAsync(
            request,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using HttpRequestMessage validRequest = new(
            HttpMethod.Get,
            "/inquiry/v1/users/oldguest00000001"
        );
        validRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            tokens.Create("oldguest00000001", "baas-Access")
        );
        using HttpResponseMessage accepted = await fixture.Client.SendAsync(
            validRequest,
            TestContext.Current.CancellationToken
        );
        accepted.EnsureSuccessStatusCode();
        using JsonDocument inquiry = (
            await accepted.Content.ReadFromJsonAsync<JsonDocument>(
                TestContext.Current.CancellationToken
            )
        )!;
        Assert.Equal("oldguest00000001", inquiry.RootElement.GetProperty("userId").GetString());
        using HttpResponseMessage keys = await fixture.Client.GetAsync(
            "/.well-known/jwks.json",
            TestContext.Current.CancellationToken
        );
        using JsonDocument body = (
            await keys.Content.ReadFromJsonAsync<JsonDocument>(
                TestContext.Current.CancellationToken
            )
        )!;
        Assert.False(body.RootElement.GetProperty("keys")[0].TryGetProperty("d", out _));
    }

    [Fact]
    public async Task FailedLogin_DoesNotLogCredentialsOrTokens()
    {
        fixture.Upstream.Mode = "forged";
        string id = DeviceId();
        using HttpResponseMessage response = await this.LoginAsync(Existing(id));
        Assert.False(response.IsSuccessStatusCode);
        Assert.DoesNotContain(
            fixture.Logs.Messages,
            message =>
                message.Contains("original-password", StringComparison.Ordinal)
                || message.Contains(id, StringComparison.Ordinal)
                || message.Contains("eyJ", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task MigrationOnlyMode_DoesNotCreateNewIdentityWhenCredentialsAreMissing()
    {
        AuthOptions options = fixture.Factory.Services.GetRequiredService<AuthOptions>();
        options.AllowNewGuests = false;
        try
        {
            using HttpResponseMessage response = await this.LoginAsync(new());
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken
            );
            Assert.DoesNotContain("idToken", body);
            Assert.DoesNotContain("createdDeviceAccount", body);
        }
        finally
        {
            options.AllowNewGuests = true;
        }
    }
}
