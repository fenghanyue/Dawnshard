using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuestAuth;

// Protocol adapted from DragaliaLostRevival/DragaliaBaasServer; see NOTICE.md.
public sealed record DeviceCredential(string Id, string Password);

public sealed class LoginRequest
{
    public DeviceCredential? DeviceAccount { get; set; }
    public string? PreviousUserId { get; set; }
    public JsonElement? IdpAccount { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Fields { get; set; }
}

public sealed record DeviceRegistration(
    string Id,
    string PasswordHash,
    string Subject,
    long CreatedAt
);

public sealed class LoginFailure(int statusCode) : Exception
{
    public int StatusCode { get; } = statusCode;
}
