using DragaliaAPI.Database.Entities;
using DragaliaAPI.Database.Utils;
using DragaliaAPI.Features.Dungeon;
using DragaliaAPI.Features.Shared.Options;
using DragaliaAPI.Infrastructure.Results;
using DragaliaAPI.Shared.MasterAsset;
using DragaliaAPI.Shared.MasterAsset.Models.Missions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DragaliaAPI.Integration.Test.Features.Event;

public sealed class RetiredWindsTest : TestFixture, IDisposable
{
    private const int EventId = 20818;
    private readonly WebApplicationFactory<Program> application;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public RetiredWindsTest(CustomWebApplicationFactory factory, ITestOutputHelper outputHelper)
        : base(factory, outputHelper)
    {
        this.application = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Configure<EventOptions>(options =>
                {
                    EventOptions production = new ConfigurationBuilder()
                        .AddJsonFile(
                            Path.Combine(AppContext.BaseDirectory, "Data", "tencentEvents.json")
                        )
                        .Build()
                        .GetSection("EventOptions")
                        .Get<EventOptions>()!;
                    options.DisabledMemoryEventIds.AddRange(production.DisabledMemoryEventIds);
                })
            )
        );
        using HttpClient original = this.Client;
        this.Client = this.application.CreateClient(new() { BaseAddress = original.BaseAddress! });
        foreach ((string name, IEnumerable<string> values) in original.DefaultRequestHeaders)
        {
            this.Client.DefaultRequestHeaders.TryAddWithoutValidation(name, values);
        }
    }

    void IDisposable.Dispose()
    {
        this.application.Dispose();
        base.Dispose();
    }

    [Theory]
    [InlineData("memory_event/activate")]
    [InlineData("build_event/get_event_data")]
    [InlineData("build_event/entry")]
    [InlineData("build_event/receive_build_point_reward")]
    [InlineData("event_summon/get_data")]
    public async Task CachedEventRequests_AreRejectedWithoutCreatingProgress(string endpoint)
    {
        // These requests all have the same event_id field on the wire.
        DragaliaResponse<ResultCodeResponse> result =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                endpoint,
                new MemoryEventActivateRequest(EventId),
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        result.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
        (
            await this.ApiContext.PlayerEventData.AnyAsync(
                x => x.ViewerId == this.ViewerId && x.EventId == EventId,
                Token
            )
        )
            .Should()
            .BeFalse();
    }

    [Theory]
    [InlineData(208180102)]
    [InlineData(208180401)]
    [InlineData(208180501)]
    public async Task DungeonStart_IsRejectedBeforeInventoryChanges(int questId)
    {
        int crystal = await this
            .ApiContext.PlayerUserData.Select(x => x.Crystal)
            .SingleAsync(Token);
        DragaliaResponse<ResultCodeResponse> result =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "dungeon_start/start",
                new DungeonStartStartRequest { QuestId = questId, PartyNoList = [1] },
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        result.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
        (await this.ApiContext.PlayerUserData.Select(x => x.Crystal).SingleAsync(Token))
            .Should()
            .Be(crystal);
        (await this.ApiContext.PlayerQuests.AnyAsync(x => x.QuestId == questId, Token))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task SkipAndExistingBattle_CannotGrantRewardsOrConsumeTickets()
    {
        DbPlayerUserData before = await this.ApiContext.PlayerUserData.SingleAsync(Token);
        DragaliaResponse<ResultCodeResponse> skip =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "dungeon_skip/start",
                new DungeonSkipStartRequest
                {
                    QuestId = 208180102,
                    PartyNo = 1,
                    PlayCount = 1,
                },
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        skip.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
        string key = this.DungeonService.CreateSession(
            new DungeonSession { QuestData = MasterAsset.QuestData[208180102], Party = [] }
        );
        await this.DungeonService.SaveSession(Token);
        DragaliaResponse<ResultCodeResponse> record =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "dungeon_record/record",
                new DungeonRecordRecordRequest { DungeonKey = key, PlayRecord = new() },
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        record.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
        DbPlayerUserData after = await this.ApiContext.PlayerUserData.SingleAsync(Token);
        after.Crystal.Should().Be(before.Crystal);
        after.QuestSkipPoint.Should().Be(before.QuestSkipPoint);
        (await this.ApiContext.PlayerQuests.AnyAsync(x => x.QuestId == 208180102, Token))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task Load_ClearsSelectionAndHidesTasksWhilePreservingProgress()
    {
        await this.ApiContext.PlayerUserData.ExecuteUpdateAsync(
            x => x.SetProperty(y => y.ActiveMemoryEventId, EventId),
            Token
        );
        await this.AddRangeToDatabase([
            new DbPlayerMission
            {
                Id = 10030101,
                Type = MissionType.MemoryEvent,
                GroupId = EventId,
                State = MissionState.Completed,
                Progress = 1,
            },
            new DbPlayerEventReward { EventId = EventId, RewardId = 1 },
            new DbPlayerEventItem
            {
                EventId = EventId,
                Id = 2081802,
                Type = 2,
                Quantity = 100,
            },
            new DbQuest
            {
                QuestId = 208180102,
                State = 3,
                PlayCount = 7,
            },
            new DbFortBuild { PlantId = (FortPlants)101101, Level = 25 },
        ]);
        int crystal = await this
            .ApiContext.PlayerUserData.Select(x => x.Crystal)
            .SingleAsync(Token);
        for (int i = 0; i < 2; i++)
        {
            LoadIndexResponse load = (
                await this.Client.PostMsgpack<LoadIndexResponse>(
                    "load/index",
                    cancellationToken: Token
                )
            ).Data;
            load.UserData.ActiveMemoryEventId.Should().Be(0);
        }

        MissionGetMissionListResponse list = (
            await this.Client.PostMsgpack<MissionGetMissionListResponse>(
                "mission/get_mission_list",
                cancellationToken: Token
            )
        ).Data;
        list.MemoryEventMissionList.Should().BeEmpty();
        DragaliaResponse<ResultCodeResponse> redeem =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "mission/receive_memory_event_reward",
                new MissionReceiveMemoryEventRewardRequest
                {
                    MemoryEventMissionIdList = [10030101],
                },
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        redeem.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
        (await this.ApiContext.PlayerUserData.SingleAsync(Token))
            .ActiveMemoryEventId.Should()
            .Be(0);
        (await this.ApiContext.PlayerUserData.Select(x => x.Crystal).SingleAsync(Token))
            .Should()
            .Be(crystal);
        DbPlayerMission mission = await this.ApiContext.PlayerMissions.SingleAsync(
            x => x.ViewerId == this.ViewerId && x.GroupId == EventId,
            Token
        );
        mission.State.Should().Be(MissionState.Completed);
        mission.Progress.Should().Be(1);
        (
            await this.ApiContext.PlayerEventRewards.CountAsync(
                x => x.ViewerId == this.ViewerId && x.EventId == EventId,
                Token
            )
        )
            .Should()
            .Be(1);
        (await this.ApiContext.PlayerEventItems.SingleAsync(x => x.Id == 2081802, Token))
            .Quantity.Should()
            .Be(100);
        (await this.ApiContext.PlayerQuests.SingleAsync(x => x.QuestId == 208180102, Token))
            .PlayCount.Should()
            .Be(7);
        (
            await this.ApiContext.PlayerFortBuilds.SingleAsync(
                x => x.PlantId == (FortPlants)101101,
                Token
            )
        )
            .Level.Should()
            .Be(25);
    }

    [Theory]
    [InlineData(2018)]
    [InlineData(2001)]
    public async Task Shop_CannotExchangeStoredCurrencyEvenWithDifferentGroup(int groupId)
    {
        await this.AddToDatabase(
            new DbPlayerEventItem
            {
                EventId = EventId,
                Id = 2081802,
                Type = 2,
                Quantity = 100,
            }
        );
        DragaliaResponse<ResultCodeResponse> result =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "event_trade/trade",
                new EventTradeTradeRequest
                {
                    TradeGroupId = groupId,
                    TradeId = 20180036,
                    TradeCount = 10,
                },
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        result.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
        (await this.ApiContext.PlayerEventItems.SingleAsync(x => x.Id == 2081802, Token))
            .Quantity.Should()
            .Be(100);
    }

    [Fact]
    public async Task OtherCompendium_CanStillBeActivatedAndRetainsSelection()
    {
        await this.Client.PostMsgpack<MemoryEventActivateResponse>(
            "memory_event/activate",
            new MemoryEventActivateRequest(20845),
            cancellationToken: Token
        );
        LoadIndexResponse load = (
            await this.Client.PostMsgpack<LoadIndexResponse>("load/index", cancellationToken: Token)
        ).Data;
        load.UserData.ActiveMemoryEventId.Should().Be(20845);
        (await this.ApiContext.PlayerEventData.AnyAsync(x => x.EventId == 20845, Token))
            .Should()
            .BeTrue();
    }
}
