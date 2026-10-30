using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GuestAuth;

public sealed class UpstreamMigration
{
    private readonly HttpClient client;
    private readonly AuthOptions options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> configuration;

    public UpstreamMigration(HttpClient client, AuthOptions options)
    {
        this.client = client;
        this.options = options;
        this.configuration = new(
            options.UpstreamUrl.TrimEnd('/') + "/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(client) { RequireHttps = true }
        );
    }

    public async Task<string> VerifyAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // The credentials are forwarded once, only for an unregistered local device.
            using HttpResponseMessage response = await this.client.PostAsJsonAsync(
                this.options.UpstreamUrl.TrimEnd('/') + "/core/v1/gateway/sdk/login",
                request,
                cancellationToken
            );
            if (
                response.StatusCode
                is System.Net.HttpStatusCode.BadRequest
                    or System.Net.HttpStatusCode.Unauthorized
            )
            {
                throw new LoginFailure(StatusCodes.Status401Unauthorized);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new LoginFailure(StatusCodes.Status503ServiceUnavailable);
            }

            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (body.Length > 65536)
            {
                throw new LoginFailure(StatusCodes.Status502BadGateway);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            string? token = document.RootElement.GetProperty("idToken").GetString();
            OpenIdConnectConfiguration config = await this.configuration.GetConfigurationAsync(
                cancellationToken
            );
            TokenValidationResult result = await new JsonWebTokenHandler().ValidateTokenAsync(
                token,
                new TokenValidationParameters
                {
                    ValidIssuer = this.options.UpstreamIssuer,
                    ValidAudience = "baas-Id",
                    IssuerSigningKeys = config.SigningKeys,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                }
            );
            if (
                !result.IsValid
                || result.SecurityToken is not JsonWebToken jwt
                || !IsIdentifier(jwt.Subject)
            )
            {
                throw new LoginFailure(StatusCodes.Status502BadGateway);
            }

            if (
                document.RootElement.GetProperty("user").GetProperty("id").GetString()
                    != jwt.Subject
                || (
                    !string.IsNullOrEmpty(request.PreviousUserId)
                    && request.PreviousUserId != jwt.Subject
                )
            )
            {
                throw new LoginFailure(StatusCodes.Status409Conflict);
            }

            return jwt.Subject;
        }
        catch (LoginFailure)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LoginFailure(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or JsonException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or ArgumentException
            )
        {
            // Never include a remote body, token, device ID or password in logs/errors.
            throw new LoginFailure(StatusCodes.Status502BadGateway);
        }
    }

    public static bool IsIdentifier(string? value) =>
        value is { Length: 16 }
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '!' or '$' or '+' or '-'
        );
}
