using DragaliaAPI.Database.Entities;
using DragaliaAPI.Database.Utils;
using DragaliaAPI.Features.Dungeon;
using DragaliaAPI.Features.Shared.Options;
using DragaliaAPI.Infrastructure.Results;
using DragaliaAPI.Shared.MasterAsset;
using DragaliaAPI.Shared.MasterAsset.Models;
using DragaliaAPI.Shared.MasterAsset.Models.Event;
using DragaliaAPI.Shared.MasterAsset.Models.Missions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DragaliaAPI.Integration.Test.Features.Event;

public class LoyaltysRequiemTest : TestFixture
{
    private const int EventId = 20458;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider clock = new(
        new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero)
    );

    public LoyaltysRequiemTest(CustomWebApplicationFactory factory, ITestOutputHelper outputHelper)
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
                            Id = EventId,
                            Start = new(2026, 10, 24, 16, 0, 0, TimeSpan.Zero),
                            End = new(2099, 1, 1, 6, 0, 0, TimeSpan.Zero),
                        }
                    );
                });
            })
        );
    }

    private async Task Enter()
    {
        await this.Client.PostMsgpack<MemoryEventActivateResponse>(
            "memory_event/activate",
            new MemoryEventActivateRequest() { EventId = EventId },
            cancellationToken: Token
        );
        await this.Client.PostMsgpack<RaidEventEntryResponse>(
            "raid_event/entry",
            new RaidEventEntryRequest(EventId),
            cancellationToken: Token
        );
    }

    private Task<int> SetItem(int id, int quantity) =>
        this
            .ApiContext.PlayerEventItems.Where(x =>
                x.ViewerId == this.ViewerId && x.EventId == EventId && x.Id == id
            )
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Quantity, quantity), Token);

    private Task<int> GetItem(int id) =>
        this
            .ApiContext.PlayerEventItems.Where(x =>
                x.ViewerId == this.ViewerId && x.EventId == EventId && x.Id == id
            )
            .Select(x => x.Quantity)
            .SingleAsync(Token);

    private async Task Claim(params int[] ids) =>
        await this.Client.PostMsgpack<RaidEventReceiveRaidPointRewardResponse>(
            "raid_event/receive_raid_point_reward",
            new RaidEventReceiveRaidPointRewardRequest(EventId, ids),
            cancellationToken: Token
        );

    private Task<DbPlayerMission> GetMission(int id, MissionType type = MissionType.MemoryEvent) =>
        this.ApiContext.PlayerMissions.SingleAsync(
            x =>
                x.ViewerId == this.ViewerId && x.GroupId == EventId && x.Id == id && x.Type == type,
            Token
        );

    [Fact]
    public async Task MemoryActivation_ExposesOriginalRewardsAsCompendiumTasks()
    {
        await this.Enter();
        MissionGetMissionListResponse response = (
            await this.Client.PostMsgpack<MissionGetMissionListResponse>(
                "mission/get_mission_list",
                cancellationToken: Token
            )
        ).Data;
        response.MemoryEventMissionList.Should().HaveCount(25);
        response.MemoryEventMissionList.Should().Contain(x => x.MemoryEventMissionId == 12080301);
        (await this.ApiContext.PlayerUserData.SingleAsync(x => x.ViewerId == this.ViewerId, Token))
            .ActiveMemoryEventId.Should()
            .Be(EventId);
        MasterAsset.EventData[EventId].IsMemoryEvent.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedPeriodAndMemoryTasks_ShareOneReward(bool periodFirst)
    {
        await this.AddToDatabase(
            new DbPlayerMission()
            {
                Id = 12080101,
                Type = MissionType.Period,
                GroupId = EventId,
                Progress = 1,
                State = MissionState.Completed,
            }
        );
        await this.Enter();
        int before = (
            await this.ApiContext.PlayerUserData.SingleAsync(
                x => x.ViewerId == this.ViewerId,
                Token
            )
        ).Crystal;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool period = attempt == 0 ? periodFirst : !periodFirst;
            DragaliaResponse<ResultCodeResponse> result;
            if (period)
            {
                result = await this.Client.PostMsgpack<ResultCodeResponse>(
                    "mission/receive_period_reward",
                    new MissionReceivePeriodRewardRequest() { PeriodMissionIdList = [12080101] },
                    ensureSuccessHeader: false,
                    cancellationToken: Token
                );
            }
            else
            {
                result = await this.Client.PostMsgpack<ResultCodeResponse>(
                    "mission/receive_memory_event_reward",
                    new MissionReceiveMemoryEventRewardRequest()
                    {
                        MemoryEventMissionIdList = [12080101],
                    },
                    ensureSuccessHeader: false,
                    cancellationToken: Token
                );
            }

            result
                .DataHeaders.ResultCode.Should()
                .Be(attempt == 0 ? ResultCode.Success : ResultCode.CommonUserStatusError);
        }

        (await this.ApiContext.PlayerUserData.SingleAsync(x => x.ViewerId == this.ViewerId, Token))
            .Crystal.Should()
            .Be(before + 50);
        (await this.GetMission(12080101)).State.Should().Be(MissionState.Claimed);
        (await this.GetMission(12080101, MissionType.Period))
            .State.Should()
            .Be(MissionState.Claimed);
    }

    [Fact]
    public void OtherOfficialMemoryRaids_KeepTheirOriginalDropRules()
    {
        EventDropService drops = this.Services.GetRequiredService<EventDropService>();
        int checkedEvents = 0;
        foreach (
            EventData evt in MasterAsset.EventData.Enumerable.Where(x =>
                x.IsMemoryEvent && x.Id != EventId
            )
        )
        {
            if (evt.EventKindType != EventKindType.Raid)
            {
                continue;
            }

            QuestData quest = MasterAsset.QuestData.Enumerable.First(x => x.Gid == evt.Id);
            drops.GenerateEventMaterialDrops(quest).Should().BeEmpty();
            checkedEvents++;
        }

        checkedEvents.Should().Be(5);
    }

    [Fact]
    public async Task Entry_InitializesAllOriginalTasksAndKeepsProgressOnReentry()
    {
        await this.Enter();
        List<DbPlayerMission> missions = await this
            .ApiContext.PlayerMissions.Where(x =>
                x.ViewerId == this.ViewerId && x.GroupId == EventId
            )
            .ToListAsync(Token);
        missions.Count(x => x.Type == MissionType.MemoryEvent).Should().Be(25);
        missions.Count(x => x.Type == MissionType.Period).Should().Be(0);
        missions.Count(x => x.Type == MissionType.Daily).Should().Be(9);
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

        (await this.GetMission(12080101)).State.Should().Be(MissionState.Completed);
        (await this.GetMission(12080201)).State.Should().Be(MissionState.Completed);
        await this.SetItem(2045801, 123);
        await this.Enter();
        (await this.GetItem(2045801)).Should().Be(123);
        (
            await this.ApiContext.PlayerMissions.CountAsync(
                x => x.ViewerId == this.ViewerId && x.GroupId == EventId,
                Token
            )
        )
            .Should()
            .Be(34);
    }

    [Fact]
    public async Task Entry_AlreadyOwnedPeleCompletesObtainDragonTask()
    {
        await this.AddToDatabase(new DbPlayerDragonData(this.ViewerId, DragonId.Pele));
        await this.Enter();
        (await this.GetMission(12080301)).State.Should().Be(MissionState.Completed);
    }

    [Fact]
    public async Task Entry_ExistingEventRecordRepairsMissingTasksAndPreservesClaimedRewards()
    {
        await this.AddToDatabase(
            new DbPlayerEventData() { EventId = EventId },
            new DbPlayerMission()
            {
                Id = 12080101,
                Type = MissionType.Period,
                GroupId = EventId,
                Progress = 1,
                State = MissionState.Claimed,
                Start = new(2022, 10, 10, 6, 0, 0, TimeSpan.Zero),
                End = new(2022, 10, 20, 6, 0, 0, TimeSpan.Zero),
            }
        );
        await this.Enter();
        DbPlayerMission participation = await this.GetMission(12080101);
        participation.State.Should().Be(MissionState.Claimed);
        participation.End.Should().Be(DateTimeOffset.UnixEpoch);
        (
            await this.ApiContext.PlayerMissions.CountAsync(
                x => x.ViewerId == this.ViewerId && x.GroupId == EventId,
                Token
            )
        )
            .Should()
            .Be(35);
        await this.Enter();
        (await this.GetMission(12080101)).State.Should().Be(MissionState.Claimed);
        EventSummonGetDataResponse summon = (
            await this.Client.PostMsgpack<EventSummonGetDataResponse>(
                "event_summon/get_data",
                new EventSummonGetDataRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        summon.BoxSummonData.RemainingQuantity.Should().Be(535);
    }

    [Fact]
    public async Task EmblemReward_BelowThresholdDoesNotGrantReward()
    {
        await this.Enter();
        await this.SetItem(2045802, 0);
        await this.Claim(1001);
        (await this.GetItem(2045801)).Should().Be(0);
        (
            await this.ApiContext.PlayerEventRewards.CountAsync(
                x => x.ViewerId == this.ViewerId && x.EventId == EventId,
                Token
            )
        )
            .Should()
            .Be(0);
    }

    [Fact]
    public async Task EmblemReward_DuplicateIdsAndRepeatedClaimGrantOnlyOnce()
    {
        await this.Enter();
        await this.SetItem(2045802, 1);
        await this.Claim(1001, 1001);
        await this.Claim(1001);
        (await this.GetItem(2045801)).Should().Be(20);
        (await this.GetItem(2045802)).Should().Be(1);
        (
            await this.ApiContext.PlayerEventRewards.CountAsync(
                x => x.ViewerId == this.ViewerId && x.EventId == EventId && x.RewardId == 1001,
                Token
            )
        )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task SilverEmblemReward_GrantsPeleAndCompletesTask()
    {
        await this.Enter();
        await this.SetItem(2045803, 100);
        int rewardId = MasterAsset
            .RaidEventReward[EventId]
            .Values.Single(x =>
                x.RewardEntityType == EntityTypes.Dragon && x.RewardEntityId == (int)DragonId.Pele
            )
            .Id;
        await this.Claim(rewardId);
        (
            await this.ApiContext.PlayerDragonData.CountAsync(
                x => x.ViewerId == this.ViewerId && x.DragonId == DragonId.Pele,
                Token
            )
        )
            .Should()
            .Be(1);
        (await this.GetMission(12080301)).State.Should().Be(MissionState.Completed);
    }

    [Fact]
    public async Task Summon_OneBlazonGrantsPeleUpdatesTaskAndAllowsReset()
    {
        await this.Enter();
        await this.SetItem(2045801, 1);
        DbPlayerEventSummonData data = await this
            .ApiContext.PlayerEventSummonData.AsTracking()
            .Include(x => x.Items)
            .SingleAsync(x => x.ViewerId == this.ViewerId && x.EventId == EventId, Token);
        foreach (int id in Enumerable.Range(2, 30))
        {
            data.Items.Add(new() { ItemId = id, TimesSummoned = 9999 });
        }

        await this.ApiContext.SaveChangesAsync(Token);
        EventSummonExecResponse response = (
            await this.Client.PostMsgpack<EventSummonExecResponse>(
                "event_summon/exec",
                new EventSummonExecRequest(EventId, 1, false),
                cancellationToken: Token
            )
        ).Data;
        response
            .BoxSummonResult.DrawDetails.Should()
            .ContainSingle()
            .Which.EntityId.Should()
            .Be((int)DragonId.Pele);
        response.BoxSummonResult.EventPoint.Should().Be(0);
        response.BoxSummonResult.ResetPossible.Should().BeTrue();
        (await this.GetMission(12080301)).State.Should().Be(MissionState.Completed);
        await this.Client.PostMsgpack<EventSummonResetResponse>(
            "event_summon/reset",
            new EventSummonResetRequest(EventId),
            cancellationToken: Token
        );
        await this.Enter();
        EventSummonGetDataResponse next = (
            await this.Client.PostMsgpack<EventSummonGetDataResponse>(
                "event_summon/get_data",
                new EventSummonGetDataRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        next.BoxSummonData.BoxSummonSeq.Should().Be(2);
        next.BoxSummonData.RemainingQuantity.Should().Be(664);
    }

    [Theory]
    [InlineData(204580101)]
    [InlineData(204580102)]
    [InlineData(204580103)]
    [InlineData(204580104)]
    [InlineData(204580105)]
    [InlineData(204580201)]
    [InlineData(204580203)]
    [InlineData(204580301)]
    [InlineData(204580303)]
    [InlineData(204580401)]
    [InlineData(204580501)]
    [InlineData(204580601)]
    [InlineData(204580602)]
    [InlineData(204580603)]
    public async Task Quest_AllAreasStartAndFirstAndRepeatClearsGrantBlazons(int questId)
    {
        await this.ImportSave();
        await this.Enter();
        for (int id = 2045805; id <= 2045807; id++)
        {
            await this.SetItem(id, 999);
        }

        await this.SetItem(2045801, 0);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int before = await this.GetItem(2045801);
            DungeonRecordRecordResponse result = await this.Clear(questId);
            result.IngameResultData.QuestId.Should().Be(questId);
            (await this.GetItem(2045801)).Should().BeGreaterThan(before);
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

    [Fact]
    public async Task DailyWrapUp_RequiresFourGoalCategoriesAndCountsEachDayOnce()
    {
        await this.ImportSave();
        await this.Enter();
        await this.SetItem(2045805, 999);
        await this.Clear(204580201);
        await this.Clear(204580201);
        await this.Clear(204580301);
        (await this.GetMission(11590501, MissionType.Daily)).Progress.Should().Be(3);
        int[] blazonRewards = MasterAsset
            .RaidEventReward[EventId]
            .Values.Where(x =>
                x.RewardEntityType == EntityTypes.RaidEventItem
                && x.RewardEntityId == 2045801
                && x.EventItemId == 2045802
            )
            .Select(x => x.Id)
            .ToArray();
        await this.SetItem(2045802, 2000);
        await this.Claim(blazonRewards);
        await this.FinishBlazonDaily();
        (await this.GetMission(11590501, MissionType.Daily))
            .State.Should()
            .Be(MissionState.Completed);
        (await this.GetMission(12080401)).State.Should().Be(MissionState.Completed);
        (await this.GetMission(12080402)).Progress.Should().Be(1);
        await this.Clear(204580201);
        (await this.GetMission(12080402)).Progress.Should().Be(1);
    }

    [Fact]
    public async Task DailyReset_AtFourteenBeijingPreservesPeriodProgressAcrossThreeDays()
    {
        DateTimeOffset boundary = new(2027, 10, 25, 6, 0, 0, TimeSpan.Zero);
        this.clock.SetUtcNow(boundary.AddMinutes(-1));
        await this.ImportSave();
        if (
            !await this.ApiContext.WallRewardDates.AnyAsync(x => x.ViewerId == this.ViewerId, Token)
        )
        {
            await this.AddToDatabase(
                new DbWallRewardDate() { LastClaimDate = DateTimeOffset.UnixEpoch }
            );
        }

        await this.Client.PostMsgpack<LoginIndexResponse>(
            "login/index",
            new LoginIndexRequest(),
            cancellationToken: Token
        );
        await this.Enter();
        await this.SetItem(2045805, 999);
        int[] emblemIds = [2045802, 2045803, 2045804];
        for (int day = 0; day < 3; day++)
        {
            if (day > 0)
            {
                this.clock.SetUtcNow(boundary.AddDays(day - 1));
                await this.Client.PostMsgpack<LoginIndexResponse>(
                    "login/index",
                    new LoginIndexRequest(),
                    cancellationToken: Token
                );
                (await this.GetMission(11590101, MissionType.Daily)).Progress.Should().Be(0);
                (await this.GetMission(11590501, MissionType.Daily)).Progress.Should().Be(0);
                (await this.GetMission(12080403)).Progress.Should().Be(day);
            }

            await this.Clear(204580201);
            await this.Clear(204580201);
            await this.Clear(204580301);
            await this.SetItem(emblemIds[day], day == 2 ? 5000 : 2000);
            int[] rewards = MasterAsset
                .RaidEventReward[EventId]
                .Values.Where(x =>
                    x.RewardEntityType == EntityTypes.RaidEventItem
                    && x.RewardEntityId == 2045801
                    && x.EventItemId == emblemIds[day]
                )
                .Select(x => x.Id)
                .ToArray();
            await this.Claim(rewards);
            await this.FinishBlazonDaily();
            (await this.GetMission(11590501, MissionType.Daily))
                .State.Should()
                .Be(MissionState.Completed);
            (await this.GetMission(12080403)).Progress.Should().Be(day + 1);
            if (day == 0)
            {
                this.clock.SetUtcNow(boundary.AddTicks(-1));
                await this.Client.PostMsgpack<LoginIndexResponse>(
                    "login/index",
                    new LoginIndexRequest(),
                    cancellationToken: Token
                );
                (await this.GetMission(11590501, MissionType.Daily))
                    .State.Should()
                    .Be(MissionState.Completed);
            }
        }

        (await this.GetMission(12080403)).State.Should().Be(MissionState.Completed);
    }

    private async Task FinishBlazonDaily()
    {
        // Quest drops vary: keep playing until the actual 300-blazon goal is met.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (
                (await this.GetMission(11590405, MissionType.Daily)).State == MissionState.Completed
            )
            {
                return;
            }

            await this.Clear(204580201);
        }

        (await this.GetMission(11590405, MissionType.Daily))
            .State.Should()
            .Be(MissionState.Completed);
    }

    private async Task<DungeonRecordRecordResponse> Clear(int questId)
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
                        Time = 100,
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
