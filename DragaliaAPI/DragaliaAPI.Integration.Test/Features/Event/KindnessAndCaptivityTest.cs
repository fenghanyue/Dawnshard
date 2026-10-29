using DragaliaAPI.Database.Entities;
using DragaliaAPI.Database.Utils;
using DragaliaAPI.Features.Dungeon;
using DragaliaAPI.Features.Shared.Options;
using DragaliaAPI.Infrastructure.Results;
using DragaliaAPI.Photon.Shared.Enums;
using DragaliaAPI.Shared.MasterAsset;
using DragaliaAPI.Shared.MasterAsset.Models.Missions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DragaliaAPI.Integration.Test.Features.Event;

public class KindnessAndCaptivityTest : TestFixture
{
    private const int EventId = 20459;
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 2, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddDays(999).AddSeconds(-1);
    private readonly FakeTimeProvider clock = new(Start.AddSeconds(-1));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public KindnessAndCaptivityTest(
        CustomWebApplicationFactory factory,
        ITestOutputHelper outputHelper
    )
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
                    options.EventList.AddRange([
                        new()
                        {
                            Id = 20801,
                            Start = Start.AddDays(-8),
                            End = Start,
                        },
                        new()
                        {
                            Id = EventId,
                            Start = Start,
                            End = End,
                        },
                    ]);
                });
            })
        );
    }

    private async Task Enter()
    {
        if (this.clock.GetUtcNow() < Start)
        {
            this.clock.SetUtcNow(Start);
        }

        await this.Client.PostMsgpack<RaidEventEntryResponse>(
            "raid_event/entry",
            new RaidEventEntryRequest(EventId),
            cancellationToken: Token
        );
    }

    private Task<DbPlayerMission> Mission(int id, MissionType type = MissionType.Period) =>
        this.ApiContext.PlayerMissions.SingleAsync(
            x => x.ViewerId == this.ViewerId && x.Id == id && x.Type == type,
            Token
        );

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(86313598, true)]
    [InlineData(86313599, false)]
    [InlineData(86313600, false)]
    public async Task Schedule_UsesActual999DayBoundary(int seconds, bool active)
    {
        this.clock.SetUtcNow(Start.AddSeconds(seconds));
        DragaliaResponse<ResultCodeResponse> response =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "raid_event/get_event_data",
                new RaidEventGetEventDataRequest(EventId),
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        response
            .DataHeaders.ResultCode.Should()
            .Be(active ? ResultCode.Success : ResultCode.EventOutThePeriod);
        if (seconds >= 0)
        {
            DragaliaResponse<ResultCodeResponse> old =
                await this.Client.PostMsgpack<ResultCodeResponse>(
                    "build_event/get_event_data",
                    new BuildEventGetEventDataRequest(20801),
                    ensureSuccessHeader: false,
                    cancellationToken: Token
                );
            old.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
        }
    }

    [Fact]
    public async Task Entry_Initializes33MissionsAndPreservesParticipationRewards()
    {
        this.clock.SetUtcNow(Start);
        RaidEventGetEventDataResponse before = (
            await this.Client.PostMsgpack<RaidEventGetEventDataResponse>(
                "raid_event/get_event_data",
                new RaidEventGetEventDataRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        before.RaidEventUserData.Should().BeNull();
        await this.Enter();
        await this.Enter();
        List<DbPlayerMission> missions = await this
            .ApiContext.PlayerMissions.Where(x =>
                x.ViewerId == this.ViewerId && x.GroupId == EventId
            )
            .ToListAsync(Token);
        missions.Count(x => x.Type == MissionType.Period).Should().Be(24);
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

        (await this.Mission(12090101)).State.Should().Be(MissionState.Completed);
        (await this.Mission(12090102)).State.Should().Be(MissionState.Completed);
        int crystal = await this
            .ApiContext.PlayerUserData.Select(x => x.Crystal)
            .SingleAsync(Token);
        await this.Client.PostMsgpack<MissionReceivePeriodRewardResponse>(
            "mission/receive_period_reward",
            new MissionReceivePeriodRewardRequest { PeriodMissionIdList = [12090101, 12090102] },
            cancellationToken: Token
        );
        (await this.ApiContext.PlayerUserData.Select(x => x.Crystal).SingleAsync(Token))
            .Should()
            .Be(crystal + 50);
        (
            await this.ApiContext.PlayerAbilityCrests.AnyAsync(
                x => x.AbilityCrestId == AbilityCrestId.BetterTogether,
                Token
            )
        )
            .Should()
            .BeTrue();
        DragaliaResponse<ResultCodeResponse> duplicate =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "mission/receive_period_reward",
                new MissionReceivePeriodRewardRequest { PeriodMissionIdList = [12090101] },
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        duplicate.DataHeaders.ResultCode.Should().Be(ResultCode.CommonUserStatusError);
    }

    [Fact]
    public async Task Prologue_GrantsMelsaAndDoesNotGrantTwice()
    {
        await this.Enter();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await this.Client.PostMsgpack<QuestReadStoryResponse>(
                "quest/read_story",
                new QuestReadStoryRequest { QuestStoryId = 2045901 },
                cancellationToken: Token
            );
        }

        DbPlayerCharaData melsa = await this.ApiContext.PlayerCharaData.SingleAsync(
            x => x.CharaId == Charas.Melsa,
            Token
        );
        melsa.IsTemporary.Should().BeTrue();
        melsa.FriendshipPoint.Should().Be(0);
    }

    [Theory]
    [InlineData(204590101)]
    [InlineData(204590102)]
    [InlineData(204590103)]
    [InlineData(204590104)]
    [InlineData(204590105)]
    [InlineData(204590201)]
    [InlineData(204590202)]
    [InlineData(204590301)]
    [InlineData(204590302)]
    [InlineData(204590401)]
    [InlineData(204590501)]
    [InlineData(204590601)]
    [InlineData(204590602)]
    public async Task EveryQuest_StartsAllAreasAndRecordsFirstAndRepeatClear(int questId)
    {
        await this.ImportSave();
        await this.Enter();
        await this.Clear(questId);
        await this.Clear(questId);
        DbQuest quest = await this.ApiContext.PlayerQuests.SingleAsync(
            x => x.QuestId == questId,
            Token
        );
        quest.State.Should().Be(3);
        quest.PlayCount.Should().Be(2);
        if (questId >= 204590301)
        {
            (await this.Mission(12090602)).Progress.Should().Be(2);
        }

        if (questId == 204590501)
        {
            (await this.Mission(12090701)).State.Should().Be(MissionState.Completed);
        }

        if (questId == 204590602)
        {
            (await this.Mission(12090901)).State.Should().Be(MissionState.Completed);
        }
    }

    [Fact]
    public async Task BoxSummon_ChargesOneBlazonGrantsSylviaAndResets()
    {
        await this.Enter();
        await this
            .ApiContext.PlayerEventItems.Where(x => x.EventId == EventId && x.Id == 2045901)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Quantity, 5000), Token);
        EventSummonGetDataResponse box = (
            await this.Client.PostMsgpack<EventSummonGetDataResponse>(
                "event_summon/get_data",
                new EventSummonGetDataRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        box.BoxSummonData.RemainingQuantity.Should().Be(535);
        box.BoxSummonData.BoxSummonDetail.Should().Contain(x => x.EntityId == (int)DragonId.Sylvia);
        int remaining = 535;
        while (remaining > 0)
        {
            int count = Math.Min(100, remaining);
            await this.Client.PostMsgpack<EventSummonExecResponse>(
                "event_summon/exec",
                new EventSummonExecRequest
                {
                    EventId = EventId,
                    Count = count,
                    IsEnableStopByTarget = false,
                },
                cancellationToken: Token
            );
            remaining -= count;
        }

        (
            await this
                .ApiContext.PlayerEventItems.Where(x => x.EventId == EventId && x.Id == 2045901)
                .Select(x => x.Quantity)
                .SingleAsync(Token)
        )
            .Should()
            .Be(4465);
        (await this.Mission(12090201)).State.Should().Be(MissionState.Completed);
        EventSummonResetResponse reset = (
            await this.Client.PostMsgpack<EventSummonResetResponse>(
                "event_summon/reset",
                new EventSummonResetRequest(EventId),
                cancellationToken: Token
            )
        ).Data;
        reset.BoxSummonData.BoxSummonSeq.Should().Be(2);
        reset.BoxSummonData.RemainingQuantity.Should().Be(664);
    }

    [Fact]
    public async Task MedalRewards_EnforceThresholdAndGrantAll195OnlyOnce()
    {
        await this.Enter();
        int[] ids = MasterAsset.RaidEventReward[EventId].Values.Select(x => x.Id).ToArray();
        ids.Should().HaveCount(195);
        await this.Client.PostMsgpack<RaidEventReceiveRaidPointRewardResponse>(
            "raid_event/receive_raid_point_reward",
            new RaidEventReceiveRaidPointRewardRequest(EventId, [1001]),
            cancellationToken: Token
        );
        (
            await this.ApiContext.PlayerEventRewards.CountAsync(
                x => x.ViewerId == this.ViewerId && x.EventId == EventId,
                Token
            )
        )
            .Should()
            .Be(0);
        await this
            .ApiContext.PlayerEventItems.Where(x => x.EventId == EventId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Quantity, 1000000), Token);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await this.Client.PostMsgpack<RaidEventReceiveRaidPointRewardResponse>(
                "raid_event/receive_raid_point_reward",
                new RaidEventReceiveRaidPointRewardRequest(EventId, [.. ids, ids[0]]),
                cancellationToken: Token
            );
            (
                await this.ApiContext.PlayerEventRewards.CountAsync(
                    x => x.ViewerId == this.ViewerId && x.EventId == EventId,
                    Token
                )
            )
                .Should()
                .Be(195);
        }
    }

    [Fact]
    public async Task DailyGoals_ResetAndCountThreeDistinctDays()
    {
        await this.ImportSave();
        if (
            !await this.ApiContext.WallRewardDates.AnyAsync(x => x.ViewerId == this.ViewerId, Token)
        )
        {
            await this.AddToDatabase(
                new DbWallRewardDate { LastClaimDate = DateTimeOffset.UnixEpoch }
            );
        }

        await this.Enter();
        for (int day = 1; day <= 3; day++)
        {
            if (day > 1)
            {
                this.clock.Advance(TimeSpan.FromDays(1));
                await this.Client.PostMsgpack<LoginIndexResponse>(
                    "login/index",
                    new LoginIndexRequest(),
                    cancellationToken: Token
                );
            }

            (await this.Mission(11600101, MissionType.Daily)).Progress.Should().Be(0);
            for (int battle = 0; battle < 3; battle++)
            {
                await this.Clear(204590202);
                await this.Clear(204590302);
            }

            (await this.Mission(11600501, MissionType.Daily))
                .State.Should()
                .Be(MissionState.Completed);
            (await this.Mission(12090403)).Progress.Should().Be(day);
            await this.Clear(204590302);
            (await this.Mission(12090403)).Progress.Should().Be(day);
        }

        (await this.Mission(12090403)).State.Should().Be(MissionState.Completed);
    }

    private async Task Clear(int questId)
    {
        DateTimeOffset now = this.clock.GetUtcNow();
        await this.ApiContext.PlayerUserData.ExecuteUpdateAsync(
            x =>
                x.SetProperty(y => y.StaminaSingle, 999)
                    .SetProperty(y => y.LastStaminaSingleUpdateTime, now),
            Token
        );
        await this
            .ApiContext.PlayerEventItems.Where(x => x.EventId == EventId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Quantity, 10000), Token);
        DungeonStartStartResponse start = (
            await this.Client.PostMsgpack<DungeonStartStartResponse>(
                "dungeon_start/start",
                new DungeonStartStartRequest { QuestId = questId, PartyNoList = [1] },
                cancellationToken: Token
            )
        ).Data;
        start.OddsInfo.Enemy.Should().NotBeEmpty();
        if (questId >= 204590301)
        {
            start.IngameData.DungeonType.Should().Be(DungeonTypes.RaidSingle);
        }

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
                    Wave = 0,
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
        if (questId >= 204590301)
        {
            (await this.ApiContext.PlayerUserData.Select(x => x.StaminaSingle).SingleAsync(Token))
                .Should()
                .Be(984);
        }
    }
}
