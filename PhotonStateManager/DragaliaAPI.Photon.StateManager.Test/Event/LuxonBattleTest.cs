using System.Net;
using System.Net.Http.Json;
using DragaliaAPI.Photon.Plugin.Plugins.GameLogic.Events;
using DragaliaAPI.Photon.Shared.Enums;
using DragaliaAPI.Photon.Shared.Models;
using DragaliaAPI.Photon.StateManager.Models;
using MessagePack;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DragaliaAPI.Photon.StateManager.Test.Event;

[Collection(TestCollection.Name)]
public sealed class LuxonBattleTest : IDisposable
{
    private readonly ApiStub stub = new();
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;
    private string? createdRoomName;

    public LuxonBattleTest(CustomWebApplicationFactory root)
    {
        this.factory = root.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.Configure<LuxonApiOptions>(options =>
                {
                    options.BaseUrl = "http://synthetic-game/2.19.0_20220714193707/";
                    options.Token = "synthetic-api-token";
                });
                services
                    .AddHttpClient<LuxonApiClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => this.stub);
            })
        );
        this.client = this.factory.CreateClient();
        this.client.DefaultRequestHeaders.Authorization = new("Bearer", "photontoken");
    }

    public void Dispose()
    {
        if (this.createdRoomName is not null)
        {
            this.factory.Services.GetRequiredService<Redis.OM.Contracts.IRedisConnectionProvider>()
                .Connection.Execute("DEL", $"{typeof(RedisGame).FullName}:{this.createdRoomName}");
        }

        this.client.Dispose();
        this.factory.Dispose();
        this.stub.Dispose();
    }

    [Fact]
    public async Task BattleEndpoints_RequireAuthentication()
    {
        this.client.DefaultRequestHeaders.Clear();
        foreach (string route in new[] { "Prepare", "Clear" })
        {
            HttpResponseMessage response = await this.client.PostAsJsonAsync(
                "/Luxon/" + route,
                new { },
                this.cancellationToken
            );
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        this.stub.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(1, 4, 0, 0, 0)]
    [InlineData(2, 2, 2, 0, 0)]
    [InlineData(3, 2, 1, 1, 0)]
    [InlineData(4, 1, 1, 1, 1)]
    public async Task Prepare_AllocatesFourUnitsAndUsesOriginalMessagePackModels(
        int players,
        int first,
        int second,
        int third,
        int fourth
    )
    {
        LuxonPrepareRequest request = new()
        {
            Actors = Actors(players),
            RoomName = Guid.NewGuid().ToString(),
        };
        HttpResponseMessage response = await this.client.PostAsJsonAsync(
            "/Luxon/Prepare",
            request,
            this.cancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        LuxonPreparedBattle prepared = (
            await response.Content.ReadFromJsonAsync<LuxonPreparedBattle>(this.cancellationToken)
        )!;
        prepared
            .MemberCounts.Values.Should()
            .Equal(new[] { first, second, third, fourth }.Take(players));
        MessagePackSerializerOptions options =
            MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);
        PartyEvent party = MessagePackSerializer.Deserialize<PartyEvent>(
            prepared.Party,
            options,
            this.cancellationToken
        );
        party.MemberCountTable.Should().BeEquivalentTo(prepared.MemberCounts);
        party.RaiseEventSequenceId.Should().Be(1);
        prepared.Characters.Should().HaveCount(players);
        foreach (byte[] bytes in prepared.Characters)
        {
            CharacterData character = MessagePackSerializer.Deserialize<CharacterData>(
                bytes,
                options,
                this.cancellationToken
            );
            character.HeroParams.Should().HaveCount(prepared.MemberCounts[character.PlayerId]);
            character.HeroParams[0].CharacterId.Should().Be(10100102);
            character.HeroParams[0].Hp.Should().Be(1001);
            character.HeroParams[0].PlusDef.Should().Be(11);
            CoopCompanionPlan plan = this
                .factory.Services.GetRequiredService<CoopCompanionPlanStore>()
                .Get(100 + character.PlayerId, request.RoomName, request.Actors[0].QuestId)!;
            plan.Units.Select(x => (x.Position, x.CharacterId, x.Hp))
                .Should()
                .Equal(character.HeroParams.Skip(1).Select(x => (x.Position, x.CharacterId, x.Hp)));
            foreach (HeroParam companion in character.HeroParams.Skip(1))
            {
                companion.Hp.Should().Be(99999);
                companion.PlusDef.Should().Be(99999);
            }

            foreach (HeroParam hero in character.HeroParams)
            {
                hero.Attack.Should().Be(2000 + hero.Position);
                hero.Defence.Should().Be(0);
                hero.RelativeHp.Should().Be(1.25f);
                hero.RelativeDef.Should().Be(0.25f);
            }

            character.HeroParamExs.Should().HaveCount(4);
            character.HeroParamExs[0].LimitOverCount.Should().Be(5);
        }
    }

    [Fact]
    public async Task Prepare_ExposesMatchingPlanInPlayerRoomAndReplacesItForSolo()
    {
        string roomName = Guid.NewGuid().ToString();
        this.createdRoomName = roomName;
        List<ActorInfo> actors = Actors(2);
        HttpResponseMessage snapshot = await this.client.PostAsJsonAsync(
            "/Event/LuxonSnapshot",
            new LuxonSnapshotRequest
            {
                Game = new GameBase
                {
                    Name = roomName,
                    RoomId = 1234567,
                    QuestId = actors[0].QuestId,
                    MatchingCompatibleId = 36,
                    MatchingType = MatchingTypes.Anyone,
                    EntryConditions = new(),
                    Players = actors
                        .Select(x => new Player
                        {
                            ActorNr = x.ActorNr,
                            ViewerId = x.ViewerId,
                            PartyNoList = x.PartySlots,
                        })
                        .ToList(),
                },
            },
            this.cancellationToken
        );
        snapshot.StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage prepare = await this.client.PostAsJsonAsync(
            "/Luxon/Prepare",
            new LuxonPrepareRequest { Actors = actors, RoomName = roomName },
            this.cancellationToken
        );
        prepare.StatusCode.Should().Be(HttpStatusCode.OK);
        ApiGame game = (
            await this.client.GetFromJsonAsync<ApiGame>(
                "/Get/ByViewerId/101",
                this.cancellationToken
            )
        )!;
        game.CompanionPlan.RoomName.Should().Be(roomName);
        game.CompanionPlan.Units.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new CoopCompanionUnit
                {
                    Position = 2,
                    CharacterId = 10100103,
                    Hp = 99999,
                }
            );
        this.factory.Services.GetRequiredService<CoopCompanionPlanStore>()
            .Get(101, "different-room", actors[0].QuestId)
            .Should()
            .BeNull();

        HttpResponseMessage solo = await this.client.PostAsJsonAsync(
            "/Luxon/Prepare",
            new LuxonPrepareRequest
            {
                Actors = Actors(1),
                RoomName = roomName,
                IsSolo = true,
            },
            this.cancellationToken
        );
        solo.StatusCode.Should().Be(HttpStatusCode.OK);
        ApiGame soloGame = (
            await this.client.GetFromJsonAsync<ApiGame>(
                "/Get/ByViewerId/101",
                this.cancellationToken
            )
        )!;
        soloGame.CompanionPlan.Units.Should().BeEmpty();
    }

    [Fact]
    public async Task Prepare_SoloPlayPreservesAllHeroStats()
    {
        HttpResponseMessage response = await this.client.PostAsJsonAsync(
            "/Luxon/Prepare",
            new LuxonPrepareRequest { Actors = Actors(1), IsSolo = true },
            this.cancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        LuxonPreparedBattle prepared = (
            await response.Content.ReadFromJsonAsync<LuxonPreparedBattle>(this.cancellationToken)
        )!;
        prepared.MemberCounts.Values.Should().Equal(4);
        CharacterData character = MessagePackSerializer.Deserialize<CharacterData>(
            prepared.Characters.Single(),
            MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block),
            this.cancellationToken
        );
        foreach (HeroParam hero in character.HeroParams)
        {
            hero.Hp.Should().Be(1000 + hero.Position);
            hero.PlusDef.Should().Be(10 + hero.Position);
            hero.Attack.Should().Be(2000 + hero.Position);
        }
    }

    [Fact]
    public async Task Prepare_BoostsEveryPartyAndPreservesFirstAvailableHero()
    {
        this.stub.FirstPosition = 2;
        this.stub.HeroCount = 3;
        List<ActorInfo> actors = Actors(2);
        foreach (ActorInfo actor in actors)
        {
            actor.PartySlots = [1, 2];
        }

        HttpResponseMessage response = await this.client.PostAsJsonAsync(
            "/Luxon/Prepare",
            new LuxonPrepareRequest { Actors = actors },
            this.cancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        LuxonPreparedBattle prepared = (
            await response.Content.ReadFromJsonAsync<LuxonPreparedBattle>(this.cancellationToken)
        )!;
        prepared.Characters.Should().HaveCount(4);
        foreach (byte[] bytes in prepared.Characters)
        {
            CharacterData character = MessagePackSerializer.Deserialize<CharacterData>(
                bytes,
                MessagePackSerializerOptions.Standard.WithCompression(
                    MessagePackCompression.Lz4Block
                ),
                this.cancellationToken
            );
            character.HeroParams.Select(x => x.Position).Should().Equal(2, 3);
            character.HeroParams[0].Hp.Should().Be(1002);
            character.HeroParams[0].PlusDef.Should().Be(12);
            character.HeroParams[1].Hp.Should().Be(99999);
            character.HeroParams[1].PlusDef.Should().Be(99999);
            character.HeroParamExs.Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task Prepare_RejectsUnsupportedQuestAndDuplicateViewer()
    {
        List<ActorInfo> actors = Actors(2);
        actors[1].ViewerId = actors[0].ViewerId;
        HttpResponseMessage duplicate = await this.client.PostAsJsonAsync(
            "/Luxon/Prepare",
            new LuxonPrepareRequest { Actors = actors },
            this.cancellationToken
        );
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actors = Actors(2);
        foreach (ActorInfo actor in actors)
        {
            actor.QuestId = 204020301;
        }

        HttpResponseMessage raid = await this.client.PostAsJsonAsync(
            "/Luxon/Prepare",
            new LuxonPrepareRequest { Actors = actors },
            this.cancellationToken
        );
        raid.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        this.stub.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Prepare_RejectsMismatchedHeroIdentity()
    {
        this.stub.BadIdentity = true;
        HttpResponseMessage response = await this.client.PostAsJsonAsync(
            "/Luxon/Prepare",
            new LuxonPrepareRequest { Actors = Actors(2) },
            this.cancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Clear_OverwritesClientIdentityAndForwardsExactApiResponse()
    {
        Dictionary<string, object?> record = new()
        {
            ["dungeon_key"] = "synthetic-dungeon",
            ["member_count"] = 99,
            ["is_host"] = true,
            ["connecting_viewer_id_list"] = new long[] { 99999 },
        };
        LuxonClearRequest request = ClearRequest(
            MessagePackSerializer.Serialize(record, cancellationToken: this.cancellationToken)
        );
        HttpResponseMessage response = await this.client.PostAsJsonAsync(
            "/Luxon/Clear",
            request,
            this.cancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        System.Text.Json.JsonElement result =
            await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
                this.cancellationToken
            );
        result.GetProperty("response").GetBytesFromBase64().Should().Equal(this.stub.Result);
        Dictionary<string, object?> submitted = MessagePackSerializer.Deserialize<
            Dictionary<string, object?>
        >(this.stub.Record!, cancellationToken: this.cancellationToken);
        Convert.ToInt64(submitted["member_count"]).Should().Be(2);
        submitted["is_host"].Should().Be(false);
        ((object[])submitted["connecting_viewer_id_list"]!)
            .Select(Convert.ToInt64)
            .Should()
            .Equal(101);
        this.stub.Viewer.Should().Be("102");
        this.stub.Authorization.Should().Be("Bearer synthetic-api-token");
        this.stub.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Clear_UpstreamFailureIsNotRetriedAndDoesNotInventResults()
    {
        this.stub.Fail = true;
        HttpResponseMessage response = await this.client.PostAsJsonAsync(
            "/Luxon/Clear",
            ClearRequest(
                MessagePackSerializer.Serialize(
                    new Dictionary<string, object?> { ["dungeon_key"] = "test" },
                    cancellationToken: this.cancellationToken
                )
            ),
            this.cancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        this.stub.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Clear_RejectsMalformedRecordBeforeCallingGameApi()
    {
        HttpResponseMessage response = await this.client.PostAsJsonAsync(
            "/Luxon/Clear",
            ClearRequest([0xc1]),
            this.cancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        this.stub.Calls.Should().Be(0);
    }

    private static LuxonClearRequest ClearRequest(byte[] record) =>
        new()
        {
            Actors = Actors(2),
            ActorNr = 2,
            MemberCount = 2,
            RoomName = "synthetic-room",
            RoomId = -1234567,
            Record = record,
        };

    private static List<ActorInfo> Actors(int count) =>
        Enumerable
            .Range(1, count)
            .Select(actor => new ActorInfo
            {
                ActorNr = actor,
                ViewerId = 100 + actor,
                PartySlots = [1],
                QuestId = 301010103,
            })
            .ToList();

    private sealed class ApiStub : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool BadIdentity { get; set; }
        public bool Fail { get; set; }
        public int FirstPosition { get; set; } = 1;
        public int HeroCount { get; set; } = 4;
        public byte[]? Record { get; private set; }
        public string? Viewer { get; private set; }
        public string? Authorization { get; private set; }
        public byte[] Result { get; } = [0x81, 0xa4, 0x64, 0x61, 0x74, 0x61, 0x80];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            this.Calls++;
            if (this.Fail)
            {
                return new(HttpStatusCode.ServiceUnavailable);
            }

            if (
                request.RequestUri!.AbsolutePath.EndsWith(
                    "heroparam/batch",
                    StringComparison.Ordinal
                )
            )
            {
                List<ActorInfo> actors = (
                    await request.Content!.ReadFromJsonAsync<List<ActorInfo>>(cancellationToken)
                )!;
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        actors.Select(actor => new HeroParamData
                        {
                            ActorNr = actor.ActorNr,
                            ViewerId = this.BadIdentity ? 99999 : actor.ViewerId,
                            HeroParamLists = actor
                                .PartySlots.Select(_ =>
                                    Enumerable
                                        .Range(this.FirstPosition, this.HeroCount)
                                        .Select(position => new HeroParam
                                        {
                                            Position = position,
                                            ExAbilityLv = 5,
                                            CharacterId = 10100101 + position,
                                            Hp = 1000 + position,
                                            PlusDef = 10 + position,
                                            Attack = 2000 + position,
                                            RelativeHp = 1.25f,
                                            RelativeDef = 0.25f,
                                        })
                                        .ToList()
                                )
                                .ToList(),
                        })
                    ),
                };
            }

            this.Record = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            this.Viewer = request.Headers.GetValues("Auth-ViewerId").Single();
            this.Authorization = request.Headers.Authorization!.ToString();
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(this.Result) };
        }
    }
}
