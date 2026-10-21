using DragaliaAPI.Database.Entities;
using DragaliaAPI.Database.Utils;
using DragaliaAPI.Features.Dungeon;
using DragaliaAPI.Infrastructure.Results;
using DragaliaAPI.Shared.MasterAsset;
using DragaliaAPI.Shared.MasterAsset.Models.Missions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DragaliaAPI.Integration.Test.Features.Event;

public class TrickOrTreasureOriginalTest : TestFixture
{
    private const int EventId = 20801;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public TrickOrTreasureOriginalTest(
        CustomWebApplicationFactory factory,
        ITestOutputHelper outputHelper
    )
        : base(factory, outputHelper)
    {
        this.Client.Dispose();
        this.Client = this.CreateClient(builder =>
            builder.ConfigureAppConfiguration(config =>
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["EventOptions:EventList:50:Id"] = "20801",
                        ["EventOptions:EventList:50:Start"] = "2020-01-01T06:00:00Z",
                        ["EventOptions:EventList:50:End"] = "2099-01-01T05:59:59Z",
                    }
                )
            )
        );
    }

    private async Task Enter()
    {
        await this.Client.PostMsgpack<BuildEventEntryResponse>(
            "build_event/entry",
            new BuildEventEntryRequest(EventId),
            cancellationToken: Token
        );
    }

    [Fact]
    public async Task GetData_BeforeEntry_RequestsEntryWithoutCreatingProgress()
    {
        BuildEventGetEventDataResponse response = (
            await this.Client.PostMsgpack<BuildEventGetEventDataResponse>(
                "build_event/get_event_data",
                new BuildEventGetEventDataRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        response.BuildEventUserData.Should().BeNull();
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
    [InlineData("build_event/get_event_data")]
    [InlineData("build_event/entry")]
    [InlineData("build_event/receive_build_point_reward")]
    public async Task UnscheduledEvent_CannotBeEnteredOrClaimed(string endpoint)
    {
        using HttpClient unscheduled = this.CreateClient();
        DragaliaResponse<ResultCodeResponse> response =
            await unscheduled.PostMsgpack<ResultCodeResponse>(
                endpoint,
                new BuildEventEntryRequest(EventId),
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        response.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
    }

    [Fact]
    public async Task Entry_InitializesOriginalMissionsAndTrade()
    {
        await this.Enter();
        BuildEventGetEventDataResponse response = (
            await this.Client.PostMsgpack<BuildEventGetEventDataResponse>(
                "build_event/get_event_data",
                new BuildEventGetEventDataRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        response.BuildEventUserData.UserBuildEventItemList.Should().HaveCount(3);
        response.EventTradeList.Should().NotBeEmpty();
        List<DbPlayerMission> missions = await this
            .ApiContext.PlayerMissions.Where(x =>
                x.ViewerId == this.ViewerId && x.GroupId == EventId
            )
            .ToListAsync(Token);
        missions.Count(x => x.Type == MissionType.Period).Should().Be(26);
        missions.Count(x => x.Type == MissionType.Daily).Should().Be(3);
        missions.Single(x => x.Id == 10020001).State.Should().Be(MissionState.Completed);
        foreach (DbPlayerMission mission in missions)
        {
            MasterAsset
                .MissionProgressionInfo.Enumerable.Should()
                .ContainSingle(x =>
                    x.MissionType == mission.Type
                    && x.MissionId == mission.Id
                    && x.CompleteType != MissionCompleteType.UnimplementedAutoComplete
                );
        }

        await this.Enter();
        (
            await this.ApiContext.PlayerMissions.CountAsync(
                x => x.ViewerId == this.ViewerId && x.GroupId == EventId,
                Token
            )
        )
            .Should()
            .Be(29);
    }

    [Theory]
    [InlineData(208010101)]
    [InlineData(208010102)]
    [InlineData(208010103)]
    [InlineData(208010104)]
    [InlineData(208010105)]
    [InlineData(208010106)]
    [InlineData(208010107)]
    [InlineData(208010201)]
    [InlineData(208010301)]
    [InlineData(208010302)]
    [InlineData(208010303)]
    [InlineData(208010401)]
    [InlineData(208010501)]
    public async Task Quest_StartsAllAreasAndRecordsFirstAndRepeatClear(int questId)
    {
        await this.ImportSave();
        await this.Enter();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            DungeonRecordRecordResponse response = await this.Clear(
                questId,
                questId == 208010501 ? 5 : 0,
                100
            );
            response.IngameResultData.QuestId.Should().Be(questId);
            response.IngameResultData.RewardRecord.TakeAccumulatePoint.Should().BeGreaterThan(0);
        }

        (
            await this.ApiContext.PlayerQuests.SingleAsync(
                x => x.ViewerId == this.ViewerId && x.QuestId == questId,
                Token
            )
        )
            .PlayCount.Should()
            .Be(2);
    }

    [Theory]
    [InlineData(4, 100, false)]
    [InlineData(5, 120.01f, false)]
    [InlineData(5, 120, true)]
    [InlineData(5, 119.99f, true)]
    public async Task Challenge_RequiresFullClearWithinTwoMinutes(
        int waves,
        float seconds,
        bool complete
    )
    {
        await this.ImportSave();
        await this.Enter();
        await this.Clear(208010501, waves, seconds);
        DbPlayerMission speed = await this.ApiContext.PlayerMissions.SingleAsync(
            x => x.ViewerId == this.ViewerId && x.Id == 10020901 && x.Type == MissionType.Period,
            Token
        );
        speed.State.Should().Be(complete ? MissionState.Completed : MissionState.InProgress);
    }

    [Fact]
    public async Task Challenge_CountsWavesAcrossRunsAndGrantsDailyChocolateProgress()
    {
        await this.ImportSave();
        await this.Enter();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await this.Clear(208010501, 5, 100);
        }

        DbPlayerMission waves = await this.ApiContext.PlayerMissions.SingleAsync(
            x => x.ViewerId == this.ViewerId && x.Id == 10020701 && x.Type == MissionType.Period,
            Token
        );
        waves.State.Should().Be(MissionState.Completed);
        waves.Progress.Should().Be(15);
        (
            await this.ApiContext.PlayerMissions.SingleAsync(
                x => x.ViewerId == this.ViewerId && x.Id == 10003101 && x.Type == MissionType.Daily,
                Token
            )
        )
            .State.Should()
            .Be(MissionState.Completed);
    }

    [Fact]
    public async Task Points_ClaimsAllOriginalRewardsOnlyOnce()
    {
        await this.Enter();
        await this
            .ApiContext.PlayerEventItems.Where(x => x.ViewerId == this.ViewerId && x.Id == 2080101)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Quantity, 1_000_000), Token);
        BuildEventReceiveBuildPointRewardResponse first = (
            await this.Client.PostMsgpack<BuildEventReceiveBuildPointRewardResponse>(
                "build_event/receive_build_point_reward",
                new BuildEventReceiveBuildPointRewardRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        first.BuildEventRewardEntityList.Should().NotBeEmpty();
        BuildEventReceiveBuildPointRewardResponse repeat = (
            await this.Client.PostMsgpack<BuildEventReceiveBuildPointRewardResponse>(
                "build_event/receive_build_point_reward",
                new BuildEventReceiveBuildPointRewardRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        repeat.BuildEventRewardEntityList.Should().BeEmpty();
        repeat.BuildEventRewardList.Should().HaveSameCount(first.BuildEventRewardList);
    }

    [Fact]
    public async Task Shop_ConsumesOriginalCurrencyAndEnforcesLimit()
    {
        await this.Enter();
        await this
            .ApiContext.PlayerEventItems.Where(x => x.ViewerId == this.ViewerId && x.Id == 2080102)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Quantity, 1000), Token);
        int manaBefore = (
            await this.ApiContext.PlayerUserData.SingleAsync(
                x => x.ViewerId == this.ViewerId,
                Token
            )
        ).ManaPoint;
        await this.Client.PostMsgpack<EventTradeTradeResponse>(
            "event_trade/trade",
            new EventTradeTradeRequest()
            {
                TradeGroupId = 2001,
                TradeId = 20010032,
                TradeCount = 100,
            },
            cancellationToken: Token
        );
        (
            await this.ApiContext.PlayerEventItems.SingleAsync(
                x => x.ViewerId == this.ViewerId && x.Id == 2080102,
                Token
            )
        )
            .Quantity.Should()
            .Be(700);
        (await this.ApiContext.PlayerUserData.SingleAsync(x => x.ViewerId == this.ViewerId, Token))
            .ManaPoint.Should()
            .Be(manaBefore + 100_000);
        DragaliaResponse<EventTradeTradeResponse> overLimit =
            await this.Client.PostMsgpack<EventTradeTradeResponse>(
                "event_trade/trade",
                new EventTradeTradeRequest()
                {
                    TradeGroupId = 2001,
                    TradeId = 20010032,
                    TradeCount = 1,
                },
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        overLimit.DataHeaders.ResultCode.Should().NotBe(ResultCode.Success);
        (
            await this.ApiContext.PlayerEventItems.SingleAsync(
                x => x.ViewerId == this.ViewerId && x.Id == 2080102,
                Token
            )
        )
            .Quantity.Should()
            .Be(700);
    }

    [Fact]
    public async Task ParticipationMission_GrantsRewardOnlyOnce()
    {
        await this.Enter();
        int before = (
            await this.ApiContext.PlayerUserData.SingleAsync(
                x => x.ViewerId == this.ViewerId,
                Token
            )
        ).Crystal;
        await this.Client.PostMsgpack<MissionReceivePeriodRewardResponse>(
            "mission/receive_period_reward",
            new MissionReceivePeriodRewardRequest() { PeriodMissionIdList = [10020001] },
            cancellationToken: Token
        );
        (await this.ApiContext.PlayerUserData.SingleAsync(x => x.ViewerId == this.ViewerId, Token))
            .Crystal.Should()
            .Be(before + 50);
        DragaliaResponse<MissionReceivePeriodRewardResponse> repeat =
            await this.Client.PostMsgpack<MissionReceivePeriodRewardResponse>(
                "mission/receive_period_reward",
                new MissionReceivePeriodRewardRequest() { PeriodMissionIdList = [10020001] },
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        repeat.DataHeaders.ResultCode.Should().NotBe(ResultCode.Success);
        (await this.ApiContext.PlayerUserData.SingleAsync(x => x.ViewerId == this.ViewerId, Token))
            .Crystal.Should()
            .Be(before + 50);
    }

    [Fact]
    public async Task Story_GrantsFacilityAndCanBuildAndUpgradeIt()
    {
        await this.Enter();
        foreach (int storyId in new[] { 2080101, 2080102, 2080103, 2080104, 2080105 })
        {
            await this.Client.PostMsgpack<QuestReadStoryResponse>(
                "quest/read_story",
                new QuestReadStoryRequest(storyId),
                cancellationToken: Token
            );
        }

        DbFortBuild facility = await this.ApiContext.PlayerFortBuilds.SingleAsync(
            x => x.ViewerId == this.ViewerId && x.PlantId == FortPlants.SweetRetreat,
            Token
        );
        facility.Level.Should().Be(1);
        long buildId = facility.BuildId;
        await this.Client.PostMsgpack<FortMoveResponse>(
            "fort/move",
            new FortMoveRequest(buildId, 5, 5),
            cancellationToken: Token
        );
        for (int level = 2; level <= 30; level++)
        {
            await this.Client.PostMsgpack<FortLevelupStartResponse>(
                "fort/levelup_start",
                new FortLevelupStartRequest((long)buildId),
                cancellationToken: Token
            );
            await this
                .ApiContext.PlayerFortBuilds.Where(x =>
                    x.ViewerId == this.ViewerId && x.BuildId == (long)buildId
                )
                .ExecuteUpdateAsync(
                    x => x.SetProperty(p => p.BuildEndDate, DateTimeOffset.UtcNow.AddSeconds(-1)),
                    Token
                );
            await this.Client.PostMsgpack<FortLevelupEndResponse>(
                "fort/levelup_end",
                new FortLevelupEndRequest((long)buildId),
                cancellationToken: Token
            );
        }

        List<DbPlayerMission> missions = await this
            .ApiContext.PlayerMissions.Where(x =>
                x.ViewerId == this.ViewerId
                && x.Type == MissionType.Period
                && (x.Id == 10020101 || x.Id == 10020201 || x.Id == 10020202 || x.Id == 10020203)
            )
            .ToListAsync(Token);
        missions.Should().HaveCount(4).And.OnlyContain(x => x.State == MissionState.Completed);
    }

    private async Task<DungeonRecordRecordResponse> Clear(int questId, int waves, float seconds)
    {
        DungeonStartStartResponse start = (
            await this.Client.PostMsgpack<DungeonStartStartResponse>(
                "dungeon_start/start",
                new DungeonStartStartRequest() { QuestId = questId, PartyNoList = [1] },
                cancellationToken: Token
            )
        ).Data;
        start.OddsInfo.Enemy.Should().NotBeEmpty();
        string key = start.IngameData.DungeonKey;
        for (int area = 1; area < start.IngameData.AreaInfoList.Count(); area++)
        {
            DungeonGetAreaOddsResponse odds = (
                await this.Client.PostMsgpack<DungeonGetAreaOddsResponse>(
                    "dungeon/get_area_odds",
                    new DungeonGetAreaOddsRequest() { DungeonKey = key, AreaIdx = area },
                    cancellationToken: Token
                )
            ).Data;
            odds.OddsInfo.Enemy.Should().NotBeEmpty();
        }

        DungeonSession session = await this.DungeonService.GetSession(key, Token);
        return (
            await this.Client.PostMsgpack<DungeonRecordRecordResponse>(
                "dungeon_record/record",
                new DungeonRecordRecordRequest()
                {
                    DungeonKey = key,
                    PlayRecord = new()
                    {
                        Time = seconds,
                        Wave = waves,
                        TreasureRecord = session
                            .EnemyList.Select(x => new AtgenTreasureRecord()
                            {
                                AreaIdx = x.Key,
                                Enemy = Enumerable.Repeat(1, x.Value.Count).ToList(),
                            })
                            .ToList(),
                        LiveUnitNoList = [1, 2, 3, 4],
                        DamageRecord = [],
                        DragonDamageRecord = [],
                        BattleRoyalRecord = new(),
                    },
                },
                cancellationToken: Token
            )
        ).Data;
    }
}
