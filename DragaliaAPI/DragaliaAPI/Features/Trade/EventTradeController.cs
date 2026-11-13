using DragaliaAPI.Features.Shared;
using DragaliaAPI.Features.Shared.Options;
using DragaliaAPI.Features.Shared.Reward;
using DragaliaAPI.Infrastructure;
using DragaliaAPI.Models.Generated;
using DragaliaAPI.Shared.Definitions.Enums;
using DragaliaAPI.Shared.MasterAsset;
using DragaliaAPI.Shared.MasterAsset.Models.Event;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DragaliaAPI.Features.Trade;

[Route("event_trade")]
public class EventTradeController(
    ITradeService tradeService,
    IUpdateDataService updateDataService,
    IRewardService rewardService,
    IOptionsMonitor<EventOptions> eventOptions
) : DragaliaControllerBase
{
    [HttpPost("get_list")]
    public async Task<DragaliaResult> GetListAll(EventTradeGetListRequest request)
    {
        this.ValidateEvent(request.TradeGroupId);
        EventTradeGetListResponse resp = new()
        {
            EventTradeList = tradeService.GetEventTradeList(request.TradeGroupId),
            UserEventItemData = new(), //await eventService.GetUserEventItemData(group.EventId);
            UserEventTradeList = await tradeService.GetUserEventTradeList(),
        };

        return Ok(resp);
    }

    [HttpPost("trade")]
    public async Task<DragaliaResult> Trade(
        EventTradeTradeRequest request,
        CancellationToken cancellationToken
    )
    {
        EventTradeTradeResponse resp = new();

        this.ValidateEvent(MasterAsset.EventTreasureTradeInfo[request.TradeId].TradeGroupId);

        await tradeService.DoTrade(TradeType.Event, request.TradeId, request.TradeCount);

        resp.UpdateDataList = await updateDataService.SaveChangesAsync(cancellationToken);
        resp.EntityResult = rewardService.GetEntityResult();
        resp.EventTradeList = tradeService.GetEventTradeList(request.TradeGroupId);
        resp.UserEventTradeList = await tradeService.GetUserEventTradeList();
        resp.UserEventItemData = new(); // await eventService.GetUserEventItemData(group.EventId);

        return Ok(resp);
    }

    private void ValidateEvent(int tradeGroupId)
    {
        if (
            MasterAsset.EventTradeGroup.TryGetValue(tradeGroupId, out EventTradeGroup? group)
            && eventOptions.CurrentValue.DisabledMemoryEventIds.Contains(group.EventId)
        )
        {
            throw new DragaliaException(ResultCode.EventOutThePeriod, "Event has ended");
        }
    }
}
