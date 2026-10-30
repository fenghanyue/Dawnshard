using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GuestAuth;

public sealed class TokenService(AuthOptions options) : IDisposable
{
    public static IReadOnlyList<string> SigningAlgorithms { get; } = [SecurityAlgorithms.RsaSha256];
    private readonly RSA rsa = RSA.Create();
    private readonly JsonWebTokenHandler handler = new();
    private RsaSecurityKey key = null!;

    public void Initialize(bool hasAccounts)
    {
        Directory.CreateDirectory(options.DataDirectory);
        string path = Path.Combine(options.DataDirectory, "signing-key.pem");
        if (!File.Exists(path))
        {
            if (hasAccounts)
            {
                throw new InvalidOperationException(
                    "Signing key is missing for an existing guest database; restore the key backup."
                );
            }

            using RSA generated = RSA.Create(3072);
            using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            using StreamWriter writer = new(file);
            writer.Write(generated.ExportRSAPrivateKeyPem());
        }

        this.rsa.ImportFromPem(File.ReadAllText(path));
        this.key = new(this.rsa)
        {
            KeyId = Base64UrlEncoder.Encode(SHA256.HashData(this.rsa.ExportSubjectPublicKeyInfo()))[
                ..24
            ],
        };
    }

    public object PublicKeys()
    {
        RSAParameters parameters = this.rsa.ExportParameters(false);
        return new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = this.key.KeyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus!),
                    e = Base64UrlEncoder.Encode(parameters.Exponent!),
                },
            },
        };
    }

    public string Create(string subject, string audience)
    {
        DateTime now = DateTime.UtcNow;
        return this.handler.CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = options.Issuer,
                Audience = audience,
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddSeconds(options.TokenLifetimeSeconds),
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = subject,
                    ["sav:a"] = false,
                    ["sav:ts"] = 0,
                    ["p:id"] = "",
                },
                SigningCredentials = new(this.key, SecurityAlgorithms.RsaSha256),
            }
        );
    }

    public async Task<string?> ValidateAccessAsync(string token)
    {
        TokenValidationResult result = await this.handler.ValidateTokenAsync(
            token,
            new TokenValidationParameters
            {
                ValidIssuer = options.Issuer,
                ValidAudience = "baas-Access",
                IssuerSigningKey = this.key,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
            }
        );
        return result.IsValid ? ((JsonWebToken)result.SecurityToken).Subject : null;
    }

    public void Dispose() => this.rsa.Dispose();
}
