using DragaliaAPI.Features.Shared;
using DragaliaAPI.Features.Shared.Reward;
using DragaliaAPI.Features.Trade;
using DragaliaAPI.Infrastructure;
using DragaliaAPI.Models.Generated;
using DragaliaAPI.Shared.MasterAsset;
using Microsoft.AspNetCore.Mvc;

namespace DragaliaAPI.Features.Event;

[Route("build_event")]
[ServiceFilter<EventValidationFilter>]
public class BuildEventController(
    IUpdateDataService updateDataService,
    IRewardService rewardService,
    IEventService eventService,
    ITradeService tradeService
) : DragaliaControllerBase
{
    [HttpPost("get_event_data")]
    public async Task<DragaliaResult> GetEventData(BuildEventGetEventDataRequest request)
    {
        BuildEventGetEventDataResponse resp = new()
        {
            BuildEventUserData = await eventService.GetBuildEventUserData(request.EventId),
        };

        if (resp.BuildEventUserData is not null)
        {
            resp.IsReceivableEventDailyBonus = await eventService.GetCustomEventFlag(
                request.EventId
            );
            resp.BuildEventRewardList = await eventService.GetEventRewardList<BuildEventRewardList>(
                request.EventId
            );
        }

        if (
            MasterAsset
                .EventTradeGroup.Enumerable.FirstOrDefault(x => x.EventId == request.EventId)
                ?.Id is
            { } tradeGroupId
        )
        {
            resp.EventTradeList = tradeService.GetEventTradeList(tradeGroupId);
        }

        return Ok(resp);
    }

    [HttpPost("entry")]
    public async Task<DragaliaResult> Entry(
        BuildEventEntryRequest request,
        CancellationToken cancellationToken
    )
    {
        await eventService.CreateEventData(request.EventId);
        UpdateDataList updateData = await updateDataService.SaveChangesAsync(cancellationToken);

        BuildEventEntryResponse resp = new()
        {
            IsReceivableEventDailyBonus = await eventService.GetCustomEventFlag(request.EventId),
            BuildEventUserData = await eventService.GetBuildEventUserData(request.EventId),
            UpdateDataList = updateData,
            EntityResult = rewardService.GetEntityResult(),
        };

        return Ok(resp);
    }

    [HttpPost("receive_build_point_reward")]
    public async Task<DragaliaResult> ReceiveBuildPointReward(
        BuildEventReceiveBuildPointRewardRequest request,
        CancellationToken cancellationToken
    )
    {
        BuildEventReceiveBuildPointRewardResponse resp = new()
        {
            BuildEventRewardEntityList = await eventService.ReceiveEventRewards(request.EventId),
            UpdateDataList = await updateDataService.SaveChangesAsync(cancellationToken),
            EntityResult = rewardService.GetEntityResult(),
            BuildEventRewardList = await eventService.GetEventRewardList<BuildEventRewardList>(
                request.EventId
            ),
        };

        return Ok(resp);
    }
}
