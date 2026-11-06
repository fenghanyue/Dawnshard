using DragaliaAPI.Database.Entities;
using DragaliaAPI.Database.Utils;
using DragaliaAPI.Features.Dungeon;
using DragaliaAPI.Features.Shared.Options;
using DragaliaAPI.Infrastructure.Results;
using DragaliaAPI.Shared.MasterAsset;
using DragaliaAPI.Shared.MasterAsset.Models.Missions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DragaliaAPI.Integration.Test.Features.Event;

public class AWishToTheWindsTest : TestFixture
{
    private const int EventId = 20818;
    private static readonly DateTimeOffset Switch = new(2026, 11, 6, 6, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider clock = new(Switch.AddSeconds(-1));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public AWishToTheWindsTest(CustomWebApplicationFactory factory, ITestOutputHelper outputHelper)
        : base(factory, outputHelper)
    {
        this.Client.Dispose();
        this.Client = this.CreateClient(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(this.clock);
                services.Configure<EventOptions>(options =>
                {
                    options.EventList.Clear();
                    options.EventList.Add(
                        new()
                        {
                            Id = 20459,
                            Start = Switch.AddDays(-8),
                            End = Switch,
                        }
                    );
                });
            })
        );
    }

    private async Task Enter() =>
        await this.Client.PostMsgpack<MemoryEventActivateResponse>(
            "memory_event/activate",
            new MemoryEventActivateRequest(EventId),
            cancellationToken: Token
        );

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(86313599, false)]
    [InlineData(86313600, false)]
    public async Task Compendium_RemainsAccessibleWhileOldEventCloses(int seconds, bool oldActive)
    {
        this.clock.SetUtcNow(Switch.AddSeconds(seconds));
        await this.Enter();
        BuildEventGetEventDataResponse memory = (
            await this.Client.PostMsgpack<BuildEventGetEventDataResponse>(
                "build_event/get_event_data",
                new BuildEventGetEventDataRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        memory.BuildEventUserData.Should().NotBeNull();
        memory.BuildEventUserData.UserBuildEventItemList.Should().HaveCount(3);
        memory.EventTradeList.Should().NotBeEmpty();
        DragaliaResponse<ResultCodeResponse> old =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "raid_event/get_event_data",
                new RaidEventGetEventDataRequest(20459),
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        old.DataHeaders.ResultCode.Should()
            .Be(oldActive ? ResultCode.Success : ResultCode.EventOutThePeriod);
    }

    [Fact]
    public async Task Entry_Unlocks23MissionsAndParticipationRewardOnlyOnce()
    {
        await this.Enter();
        await this.Enter();
        List<DbPlayerMission> missions = await this
            .ApiContext.PlayerMissions.Where(x =>
                x.ViewerId == this.ViewerId && x.GroupId == EventId
            )
            .ToListAsync(Token);
        missions.Should().HaveCount(23).And.OnlyContain(x => x.Type == MissionType.MemoryEvent);
        foreach (DbPlayerMission mission in missions)
        {
            MasterAsset
                .MissionProgressionInfo.Enumerable.Should()
                .ContainSingle(x =>
                    x.MissionType == MissionType.MemoryEvent
                    && x.MissionId == mission.Id
                    && x.CompleteType != MissionCompleteType.UnimplementedAutoComplete
                );
        }

        missions.Single(x => x.Id == 10030101).State.Should().Be(MissionState.Completed);
        int before = await this.ApiContext.PlayerUserData.Select(x => x.Crystal).SingleAsync(Token);
        await this.Client.PostMsgpack<MissionReceiveMemoryEventRewardResponse>(
            "mission/receive_memory_event_reward",
            new MissionReceiveMemoryEventRewardRequest { MemoryEventMissionIdList = [10030101] },
            cancellationToken: Token
        );
        (await this.ApiContext.PlayerUserData.Select(x => x.Crystal).SingleAsync(Token))
            .Should()
            .Be(before + 50);
        DragaliaResponse<ResultCodeResponse> repeat =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "mission/receive_memory_event_reward",
                new MissionReceiveMemoryEventRewardRequest
                {
                    MemoryEventMissionIdList = [10030101],
                },
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        repeat.DataHeaders.ResultCode.Should().NotBe(ResultCode.Success);
    }

    [Fact]
    public async Task Prologue_GrantsOnlyOneWindShrineAndFacilityCanUpgrade()
    {
        await this.Enter();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await this.Client.PostMsgpack<QuestReadStoryResponse>(
                "quest/read_story",
                new QuestReadStoryRequest { QuestStoryId = 2081801 },
                cancellationToken: Token
            );
        }

        FortGetDataResponse fort = (
            await this.Client.PostMsgpack<FortGetDataResponse>(
                "fort/get_data",
                cancellationToken: Token
            )
        ).Data;
        BuildList shrine = fort
            .BuildList.Should()
            .ContainSingle(x => x.PlantId == FortPlants.WindShrine)
            .Which;
        shrine.Level.Should().Be(1);
        FortLevelupStartResponse upgrade = (
            await this.Client.PostMsgpack<FortLevelupStartResponse>(
                "fort/levelup_start",
                new FortLevelupStartRequest((long)shrine.BuildId),
                cancellationToken: Token
            )
        ).Data;
        this.clock.SetUtcNow(upgrade.UpdateDataList.BuildList!.Single().BuildEndDate.AddSeconds(1));
        FortLevelupEndResponse completed = (
            await this.Client.PostMsgpack<FortLevelupEndResponse>(
                "fort/levelup_end",
                new FortLevelupEndRequest((long)shrine.BuildId),
                cancellationToken: Token
            )
        ).Data;
        completed
            .UpdateDataList.BuildList!.Should()
            .ContainSingle(x => x.PlantId == FortPlants.WindShrine)
            .Which.Level.Should()
            .Be(2);
    }

    [Theory]
    [InlineData(208180102)]
    [InlineData(208180104)]
    [InlineData(208180301)]
    [InlineData(208180302)]
    [InlineData(208180303)]
    [InlineData(208180401)]
    [InlineData(208180501)]
    [InlineData(208180502)]
    public async Task EveryQuest_StartsAllAreasAndRecordsFirstAndRepeatClear(int questId)
    {
        await this.ImportSave();
        await this.Enter();
        await this
            .ApiContext.PlayerQuests.Where(x => x.QuestId == questId)
            .ExecuteDeleteAsync(Token);
        await this.Clear(questId);
        await this.Clear(questId);
        DbQuest quest = await this.ApiContext.PlayerQuests.SingleAsync(
            x => x.QuestId == questId,
            Token
        );
        quest.State.Should().Be(3);
        quest.PlayCount.Should().Be(2);
        (await this.ApiContext.PlayerEventItems.SingleAsync(x => x.Id == 2081801, Token))
            .Quantity.Should()
            .BeGreaterThan(0);
    }

    [Fact]
    public async Task Points_ClaimsAll98RewardsOnlyOnce()
    {
        await this.Enter();
        await this
            .ApiContext.PlayerEventItems.Where(x => x.Id == 2081801)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Quantity, 1_000_000), Token);
        BuildEventReceiveBuildPointRewardResponse first = (
            await this.Client.PostMsgpack<BuildEventReceiveBuildPointRewardResponse>(
                "build_event/receive_build_point_reward",
                new BuildEventReceiveBuildPointRewardRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        first.BuildEventRewardList.Should().HaveCount(98);
        first.BuildEventRewardEntityList.Should().HaveCount(98);
        BuildEventReceiveBuildPointRewardResponse repeat = (
            await this.Client.PostMsgpack<BuildEventReceiveBuildPointRewardResponse>(
                "build_event/receive_build_point_reward",
                new BuildEventReceiveBuildPointRewardRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        repeat.BuildEventRewardEntityList.Should().BeEmpty();
        repeat.BuildEventRewardList.Should().HaveCount(98);
    }

    [Fact]
    public async Task Shop_ExchangesEventCurrencyForFacilityMaterials()
    {
        await this.Enter();
        int before = (
            await this.ApiContext.PlayerMaterials.SingleAsync(
                x => x.MaterialId == Materials.WindwhistleGrass,
                Token
            )
        ).Quantity;
        await this
            .ApiContext.PlayerEventItems.Where(x => x.Id == 2081802)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Quantity, 100), Token);
        await this.Client.PostMsgpack<EventTradeTradeResponse>(
            "event_trade/trade",
            new EventTradeTradeRequest
            {
                TradeGroupId = 2018,
                TradeId = 20180036,
                TradeCount = 10,
            },
            cancellationToken: Token
        );
        (await this.ApiContext.PlayerEventItems.SingleAsync(x => x.Id == 2081802, Token))
            .Quantity.Should()
            .Be(90);
        (
            await this.ApiContext.PlayerMaterials.SingleAsync(
                x => x.MaterialId == Materials.WindwhistleGrass,
                Token
            )
        )
            .Quantity.Should()
            .Be(before + 40);
    }

    private async Task Clear(int questId)
    {
        await this.ApiContext.PlayerUserData.ExecuteUpdateAsync(
            x =>
                x.SetProperty(y => y.StaminaSingle, 999)
                    .SetProperty(y => y.LastStaminaSingleUpdateTime, this.clock.GetUtcNow()),
            Token
        );
        DungeonStartStartResponse start = (
            await this.Client.PostMsgpack<DungeonStartStartResponse>(
                "dungeon_start/start",
                new DungeonStartStartRequest { QuestId = questId, PartyNoList = [1] },
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
                    new DungeonGetAreaOddsRequest { DungeonKey = key, AreaIdx = area },
                    cancellationToken: Token
                )
            ).Data;
            odds.OddsInfo.Enemy.Should().NotBeEmpty();
        }

        DungeonSession session = await this.DungeonService.GetSession(key, Token);
        await this.Client.PostMsgpack<DungeonRecordRecordResponse>(
            "dungeon_record/record",
            new DungeonRecordRecordRequest
            {
                DungeonKey = key,
                PlayRecord = new()
                {
                    Time = 50,
                    Wave = questId >= 208180501 ? 5 : 0,
                    TreasureRecord = session
                        .EnemyList.Select(x => new AtgenTreasureRecord
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
        );
    }
}
