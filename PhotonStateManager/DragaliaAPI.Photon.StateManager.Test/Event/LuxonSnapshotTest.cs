using System.Net;
using System.Net.Http.Json;
using DragaliaAPI.Photon.Shared.Enums;
using DragaliaAPI.Photon.Shared.Models;
using DragaliaAPI.Photon.StateManager.Models;
using DragaliaAPI.Photon.StateManager.Test.Helpers;

namespace DragaliaAPI.Photon.StateManager.Test.Event;

public class LuxonSnapshotTest(CustomWebApplicationFactory factory, ITestOutputHelper outputHelper)
    : TestFixture(factory, outputHelper)
{
    private const string Endpoint = "/Event/LuxonSnapshot";

    [Fact]
    public async Task LuxonSnapshot_RequiresAuthentication()
    {
        this.Client.DefaultRequestHeaders.Clear();
        HttpResponseMessage response = await this.Client.PostAsJsonAsync(
            Endpoint,
            CreateSnapshot(),
            TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LuxonSnapshot_RepeatedAndNewerSnapshotsReplacePlayersAndKeepLease()
    {
        LuxonSnapshotRequest request = CreateSnapshot();
        await this.PostSnapshot(request);
        await this.PostSnapshot(request);
        (await this.RedisConnectionProvider.GetGame(request.Game.Name))!
            .Players.Should()
            .ContainSingle();

        request.Game.Players.Add(
            new()
            {
                ActorNr = 2,
                ViewerId = 102,
                PartyNoList = [2],
            }
        );
        await this.PostSnapshot(request);
        (await this.RedisConnectionProvider.GetGame(request.Game.Name))!
            .Players.Should()
            .HaveCount(2);

        request.Game.Players.RemoveAt(1);
        request.Game.MatchingType = MatchingTypes.ById;
        await this.PostSnapshot(request);
        RedisGame? stored = await this.RedisConnectionProvider.GetGame(request.Game.Name);
        stored!.Players.Should().ContainSingle();
        stored.MatchingType.Should().Be(MatchingTypes.ById);
        stored.StartEntryTime.Should().Be(request.Game.StartEntryTime);
        stored.EntryConditions.RequiredPartyPower.Should().Be(11700);

        long ttl = (
            await this.RedisConnectionProvider.Connection.ExecuteAsync(
                "TTL",
                $"{typeof(RedisGame).FullName}:{request.Game.Name}"
            )
        ).ToInt64(System.Globalization.CultureInfo.InvariantCulture);
        ttl.Should().BeInRange(1, 60);
    }

    [Fact]
    public async Task LuxonSnapshot_RejectsDuplicateViewerAndMissingHost()
    {
        LuxonSnapshotRequest request = CreateSnapshot();
        request.Game.Players.Add(
            new()
            {
                ActorNr = 2,
                ViewerId = 101,
                PartyNoList = [1],
            }
        );
        HttpResponseMessage duplicate = await this.Client.PostAsJsonAsync(
            Endpoint,
            request,
            TestContext.Current.CancellationToken
        );
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        request.Game.Players.RemoveAt(0);
        HttpResponseMessage noHost = await this.Client.PostAsJsonAsync(
            Endpoint,
            request,
            TestContext.Current.CancellationToken
        );
        noHost.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task PostSnapshot(LuxonSnapshotRequest request)
    {
        HttpResponseMessage response = await this.Client.PostAsJsonAsync(
            Endpoint,
            request,
            TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static LuxonSnapshotRequest CreateSnapshot() =>
        new()
        {
            Game = new GameBase
            {
                Name = "luxon-snapshot-test",
                RoomId = 1234567,
                QuestId = 301010103,
                MatchingCompatibleId = 36,
                MatchingType = MatchingTypes.Anyone,
                EntryConditions = new() { RequiredPartyPower = 11700 },
                Players =
                [
                    new()
                    {
                        ActorNr = 1,
                        ViewerId = 101,
                        PartyNoList = [1],
                    },
                ],
            },
        };
}
