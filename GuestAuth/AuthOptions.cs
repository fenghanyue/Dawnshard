namespace GuestAuth;

public sealed class AuthOptions
{
    public string ConnectionString { get; set; } = "";
    public string DataDirectory { get; set; } = "/data";
    public string Issuer { get; set; } = "DawnshardGuest";
    public string UpstreamUrl { get; set; } = "https://baas.lukefz.xyz";
    public string UpstreamIssuer { get; set; } = "LukeFZ";
    public int TokenLifetimeSeconds { get; set; } = 3600;
    public bool AllowNewGuests { get; set; }
}
