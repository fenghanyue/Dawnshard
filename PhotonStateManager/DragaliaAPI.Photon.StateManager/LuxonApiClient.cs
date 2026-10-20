using System.Net.Http.Headers;
using System.Net.Http.Json;
using DragaliaAPI.Photon.Shared.Models;
using DragaliaAPI.Photon.StateManager.Models;
using Microsoft.Extensions.Options;

namespace DragaliaAPI.Photon.StateManager;

/// <summary>Private bridge to the existing game API; never retries reward writes.</summary>
public sealed class LuxonApiClient(HttpClient client, IOptions<LuxonApiOptions> options)
{
    public async Task<List<HeroParamData>> GetHeroes(
        List<ActorInfo> actors,
        CancellationToken cancellationToken
    )
    {
        using HttpRequestMessage request = this.CreateRequest("heroparam/batch");
        request.Content = JsonContent.Create(actors);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<HeroParamData>>(cancellationToken)
            ?? throw new HttpRequestException("Empty hero response");
    }

    public async Task<byte[]> Record(
        LuxonClearRequest clear,
        byte[] body,
        CancellationToken cancellationToken
    )
    {
        using HttpRequestMessage request = this.CreateRequest("dungeon_record/record_multi");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            options.Value.Token
        );
        request.Headers.Add(
            "Auth-ViewerId",
            clear
                .Actors.Single(x => x.ActorNr == clear.ActorNr)
                .ViewerId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        request.Headers.Add("RoomName", clear.RoomName);
        request.Headers.Add(
            "RoomId",
            clear.RoomId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private HttpRequestMessage CreateRequest(string path)
    {
        LuxonApiOptions settings = options.Value;
        if (
            !Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out Uri? baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttp
            || string.IsNullOrWhiteSpace(settings.Token)
        )
        {
            throw new InvalidOperationException("Game API bridge is not configured");
        }

        return new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path));
    }
}
