using DragaliaAPI.Database.Entities;
using DragaliaAPI.Database.Utils;
using DragaliaAPI.Infrastructure.Results;
using DragaliaAPI.Shared.MasterAsset;
using DragaliaAPI.Shared.MasterAsset.Models.Missions;
using Microsoft.EntityFrameworkCore;

namespace DragaliaAPI.Integration.Test.Features.Event;

public class WithdrawnCompendiumTest : TestFixture
{
    public WithdrawnCompendiumTest(
        CustomWebApplicationFactory factory,
        ITestOutputHelper outputHelper
    )
        : base(factory, outputHelper) { }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(20458)]
    public async Task Activate_WithdrawnTrialIsRejected(int eventId)
    {
        MasterAsset.EventData[eventId].IsMemoryEvent.Should().BeFalse();
        DragaliaResponse<ResultCodeResponse> result =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                "memory_event/activate",
                new MemoryEventActivateRequest { EventId = eventId },
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        result.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
        (await this.ApiContext.PlayerEventData.AnyAsync(x => x.EventId == eventId, Token))
            .Should()
            .BeFalse();
    }

    [Theory]
    [InlineData(MissionType.MemoryEvent)]
    [InlineData(MissionType.Period)]
    [InlineData(MissionType.Daily)]
    public async Task RetiredTasks_AreHiddenAndCannotBeClaimed(MissionType type)
    {
        int id = type == MissionType.Daily ? 11590101 : 12080101;
        await this.AddToDatabase(
            new DbPlayerMission
            {
                Id = id,
                Type = type,
                GroupId = 20458,
                State = MissionState.Completed,
                Progress = 3,
            }
        );
        if (type == MissionType.Daily)
        {
            await this.AddToDatabase(
                new DbCompletedDailyMission
                {
                    Id = id,
                    Date = DateOnly.FromDateTime(this.LastDailyReset.UtcDateTime),
                    Progress = 3,
                    EndDate = DateTimeOffset.MaxValue,
                }
            );
        }

        MissionGetMissionListResponse list = (
            await this.Client.PostMsgpack<MissionGetMissionListResponse>(
                "mission/get_mission_list",
                cancellationToken: Token
            )
        ).Data;
        list.MemoryEventMissionList.Should().BeEmpty();
        list.PeriodMissionList.Should().NotContain(x => x.PeriodMissionId == id);
        list.DailyMissionList.Should().NotContain(x => x.DailyMissionId == id);
        string endpoint;
        object request;
        switch (type)
        {
            case MissionType.MemoryEvent:
                endpoint = "receive_memory_event_reward";
                request = new MissionReceiveMemoryEventRewardRequest
                {
                    MemoryEventMissionIdList = [id],
                };
                break;
            case MissionType.Period:
                endpoint = "receive_period_reward";
                request = new MissionReceivePeriodRewardRequest { PeriodMissionIdList = [id] };
                break;
            default:
                endpoint = "receive_daily_reward";
                request = new MissionReceiveDailyRewardRequest
                {
                    MissionParamsList =
                    [
                        new AtgenMissionParamsList(
                            id,
                            DateOnly.FromDateTime(this.LastDailyReset.UtcDateTime)
                        ),
                    ],
                };
                break;
        }

        int crystal = await this
            .ApiContext.PlayerUserData.Select(x => x.Crystal)
            .SingleAsync(Token);
        DragaliaResponse<ResultCodeResponse> result =
            await this.Client.PostMsgpack<ResultCodeResponse>(
                $"mission/{endpoint}",
                request,
                ensureSuccessHeader: false,
                cancellationToken: Token
            );
        result.DataHeaders.ResultCode.Should().Be(ResultCode.EventOutThePeriod);
        (await this.ApiContext.PlayerUserData.Select(x => x.Crystal).SingleAsync(Token))
            .Should()
            .Be(crystal);
        (
            await this.ApiContext.PlayerMissions.SingleAsync(
                x => x.ViewerId == this.ViewerId && x.Id == id && x.Type == type,
                Token
            )
        )
            .State.Should()
            .Be(MissionState.Completed);
    }
}
