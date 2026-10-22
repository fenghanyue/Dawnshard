using DragaliaAPI.Database.Entities;
using DragaliaAPI.Database.Utils;
using DragaliaAPI.Features.Dungeon;
using DragaliaAPI.Features.Shared.Options;
using DragaliaAPI.Infrastructure.Results;
using DragaliaAPI.Shared.Definitions.Enums.EventItemTypes;
using DragaliaAPI.Shared.MasterAsset.Models.Missions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DragaliaAPI.Integration.Test.Features.Event;

public class ArchivedRaidEventTest : TestFixture
{
    private const int EventId = 20401;
    private readonly FakeTimeProvider timeProvider = new(
        new DateTimeOffset(2026, 10, 22, 7, 0, 0, TimeSpan.Zero)
    );

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ArchivedRaidEventTest(
        CustomWebApplicationFactory factory,
        ITestOutputHelper outputHelper
    )
        : base(factory, outputHelper)
    {
        this.Client.Dispose();
        this.Client = this.CreateClient(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(this.timeProvider);
                services.Configure<EventOptions>(options =>
                {
                    options.EventList.Clear();
                    options.ArchivedEventIds.Add(EventId);
                });
            })
        );
    }

    [Fact]
    public async Task Activate_InitializesOriginalEventWithoutLiveSchedule_AndIsIdempotent()
    {
        await this.Activate();
        await this.Activate();
        RaidEventGetEventDataResponse response = (
            await this.Client.PostMsgpack<RaidEventGetEventDataResponse>(
                "raid_event/get_event_data",
                new RaidEventGetEventDataRequest(EventId),
                cancellationToken: Token
            )
        ).Data;

        response.RaidEventUserData.Should().NotBeNull();
        response.RaidEventUserData!.RaidEventId.Should().Be(EventId);
        (
            await this.ApiContext.PlayerEventItems.CountAsync(
                x => x.ViewerId == this.ViewerId && x.EventId == EventId,
                Token
            )
        )
            .Should()
            .Be(5);
        List<DbPlayerMission> missions = await this
            .ApiContext.PlayerMissions.Where(x =>
                x.ViewerId == this.ViewerId && x.GroupId == EventId
            )
            .ToListAsync(Token);
        missions.Count(x => x.Type == MissionType.Period).Should().Be(14);
        missions.Count(x => x.Type == MissionType.Daily).Should().Be(3);
        missions
            .Where(x => x.Type == MissionType.Period)
            .Should()
            .OnlyContain(x =>
                x.Start == DateTimeOffset.UnixEpoch && x.End == DateTimeOffset.UnixEpoch
            );
        (await this.ApiContext.PlayerUserData.SingleAsync(x => x.ViewerId == this.ViewerId, Token))
            .ActiveMemoryEventId.Should()
            .Be(EventId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activate_PreservesQuestProgress_WithOrWithoutExistingParticipation(
        bool participated
    )
    {
        await this.AddToDatabase(
            new DbQuest
            {
                QuestId = 204010101,
                State = 3,
                PlayCount = 7,
                IsMissionClear1 = true,
                BestClearTime = 42,
            }
        );
        if (participated)
        {
            await this.AddToDatabase(new DbPlayerEventData { EventId = EventId });
        }

        await this.Activate();
        await this.Activate();
        DbQuest quest = await this.ApiContext.PlayerQuests.SingleAsync(
            x => x.ViewerId == this.ViewerId && x.QuestId == 204010101,
            Token
        );
        quest.State.Should().Be(3);
        quest.PlayCount.Should().Be(7);
        quest.IsMissionClear1.Should().BeTrue();
        quest.BestClearTime.Should().Be(42);
    }

    [Fact]
    public async Task Activate_ReopensExpiredMissionsWithoutResettingClaimedRewardsOrCurrency()
    {
        await this.AddToDatabase(
            new DbPlayerEventData { EventId = EventId },
            new DbPlayerMission
            {
                Id = 10000601,
                Type = MissionType.Period,
                GroupId = EventId,
                Progress = 5,
                State = MissionState.Claimed,
                Start = new(2026, 10, 18, 6, 0, 0, TimeSpan.Zero),
                End = new(2026, 10, 21, 15, 59, 59, TimeSpan.Zero),
            },
            new DbPlayerEventItem
            {
                EventId = EventId,
                Id = 2040101,
                Type = (int)RaidEventItemType.SummonPoint,
                Quantity = 42,
            },
            new DbPlayerEventReward { EventId = EventId, RewardId = 1001 },
            new DbPlayerEventSummonData { EventId = EventId, BoxNumber = 2 }
        );

        await this.Activate();
        await this.Activate();
        DbPlayerMission mission = await this.ApiContext.PlayerMissions.SingleAsync(
            x => x.ViewerId == this.ViewerId && x.Type == MissionType.Period && x.Id == 10000601,
            Token
        );
        mission.Progress.Should().Be(5);
        mission.State.Should().Be(MissionState.Claimed);
        mission.End.Should().Be(DateTimeOffset.UnixEpoch);
        (await this.GetItemQuantity(RaidEventItemType.SummonPoint)).Should().Be(42);
        (
            await this.ApiContext.PlayerEventRewards.CountAsync(
                x => x.ViewerId == this.ViewerId && x.EventId == EventId && x.RewardId == 1001,
                Token
            )
        )
            .Should()
            .Be(1);

        EventSummonGetDataResponse summon = (
            await this.Client.PostMsgpack<EventSummonGetDataResponse>(
                "event_summon/get_data",
                new EventSummonGetDataRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        summon.BoxSummonData.BoxSummonSeq.Should().Be(2);
        summon.BoxSummonData.BoxSummonDetail.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Login_ResetsArchivedDailyMissions_AfterOriginalEventHasEnded()
    {
        await this.Activate();
        await this
            .ApiContext.PlayerMissions.Where(x =>
                x.ViewerId == this.ViewerId && x.GroupId == EventId && x.Type == MissionType.Daily
            )
            .ExecuteUpdateAsync(x => x.SetProperty(m => m.State, MissionState.Claimed), Token);
        this.timeProvider.Advance(TimeSpan.FromDays(1));
        await this.Client.PostMsgpack<LoginIndexResponse>(
            "login/index",
            new LoginIndexRequest(),
            cancellationToken: Token
        );

        List<DbPlayerMission> daily = await this
            .ApiContext.PlayerMissions.Where(x =>
                x.ViewerId == this.ViewerId && x.GroupId == EventId && x.Type == MissionType.Daily
            )
            .ToListAsync(Token);
        daily
            .Should()
            .HaveCount(3)
            .And.OnlyContain(x =>
                x.Start == new DateTimeOffset(2026, 10, 23, 6, 0, 0, TimeSpan.Zero)
                && x.End == new DateTimeOffset(2026, 10, 24, 6, 0, 0, TimeSpan.Zero)
                && x.State != MissionState.Claimed
            );
    }

    [Fact]
    public async Task UnarchivedExpiredEvent_RemainsUnavailable()
    {
        DragaliaResponse<ResultCodeResponse> response =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "build_event/get_event_data",
                new BuildEventGetEventDataRequest(20801),
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        response.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
    }

    [Theory]
    [InlineData(204010101)]
    [InlineData(204010102)]
    [InlineData(204010103)]
    [InlineData(204010104)]
    [InlineData(204010105)]
    [InlineData(204010201)]
    [InlineData(204010202)]
    [InlineData(204010203)]
    [InlineData(204010301)]
    [InlineData(204010302)]
    [InlineData(204010303)]
    [InlineData(204010401)]
    public async Task OriginalQuests_StartAllAreas_RecordRepeatClear_AndStillAwardBlazons(
        int questId
    )
    {
        await this.ImportSave();
        await this.Activate();
        await this
            .ApiContext.PlayerEventItems.Where(x =>
                x.ViewerId == this.ViewerId
                && x.EventId == EventId
                && x.Type == (int)RaidEventItemType.AdventItem1
            )
            .ExecuteUpdateAsync(x => x.SetProperty(i => i.Quantity, 10000), Token);
        int before = await this.GetItemQuantity(RaidEventItemType.SummonPoint);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            DungeonRecordRecordResponse response = await this.Clear(questId);
            response.IngameResultData.QuestId.Should().Be(questId);
        }

        (await this.GetItemQuantity(RaidEventItemType.SummonPoint)).Should().BeGreaterThan(before);
    }

    private Task<DragaliaResponse<MemoryEventActivateResponse>> Activate() =>
        this.Client.PostMsgpack<MemoryEventActivateResponse>(
            "memory_event/activate",
            new MemoryEventActivateRequest(EventId),
            cancellationToken: Token
        );

    private Task<int> GetItemQuantity(RaidEventItemType type) =>
        this
            .ApiContext.PlayerEventItems.Where(x =>
                x.ViewerId == this.ViewerId && x.EventId == EventId && x.Type == (int)type
            )
            .Select(x => x.Quantity)
            .SingleAsync(Token);

    private async Task<DungeonRecordRecordResponse> Clear(int questId)
    {
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
        return (
            await this.Client.PostMsgpack<DungeonRecordRecordResponse>(
                "dungeon_record/record",
                new DungeonRecordRecordRequest
                {
                    DungeonKey = key,
                    PlayRecord = new()
                    {
                        Time = 60,
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
            )
        ).Data;
    }
}
