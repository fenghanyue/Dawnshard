namespace DragaliaAPI.Features.GuestAuth;

public sealed class GuestAuthOptions
{
    public bool Enabled { get; set; }
    public bool ClientEnabled { get; set; }
    public string InternalUrl { get; set; } = "http://guestauth:8080";
    public string Issuer { get; set; } = "DawnshardGuest";
    public List<string> TrustedProxyAddresses { get; set; } = [];
}
