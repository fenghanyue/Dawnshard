using DragaliaAPI.Features.GuestAuth;
using DragaliaAPI.Features.Shared.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace DragaliaAPI.Infrastructure.Authentication;

/// <summary>
/// Allows setting additional properties on <see cref="JwtBearerOptions"/> using DI.
/// </summary>
/// <remarks>
/// Sourced from: <see href="https://gist.github.com/xiaomi7732/20ff2ad11b085a851759d3835b95c8d7"/>
/// </remarks>
public class ConfigureJwtBearerOptions(
    IOptions<BaasOptions> baasOptions,
    IOptions<GuestAuthOptions> guestOptions
) : IConfigureNamedOptions<JwtBearerOptions>
{
    // Never called
    public void Configure(JwtBearerOptions options) =>
        this.Configure(JwtBearerDefaults.AuthenticationScheme, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name == AuthConstants.SchemeNames.GuestJwt)
        {
            options.Authority = guestOptions.Value.InternalUrl;
            options.RequireHttpsMetadata = false; // Only the private Docker service is used for key discovery.
            options.BackchannelTimeout = TimeSpan.FromSeconds(3);
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
            };
            options.TokenValidationParameters = new()
            {
                ValidAudience = "baas-Id",
                ValidIssuer = guestOptions.Value.Issuer,
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = ["RS256"],
            };
            return;
        }

        options.Authority = baasOptions.Value.BaasUrl;
        options.TokenValidationParameters = new()
        {
            ValidAudience = baasOptions.Value.TokenAudience,
            ValidIssuer = baasOptions.Value.TokenIssuer,
        };
    }
}
