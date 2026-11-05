using DragaliaAPI.Photon.Shared.Models;
using Microsoft.Extensions.Caching.Memory;

namespace DragaliaAPI.Photon.StateManager;

public sealed class CoopCompanionPlanStore(IMemoryCache cache)
{
    public void Set(long viewerId, CoopCompanionPlan plan) =>
        cache.Set((typeof(CoopCompanionPlan), viewerId), plan, TimeSpan.FromMinutes(5));

    public CoopCompanionPlan? Get(long viewerId, string roomName, int questId) =>
        cache.TryGetValue((typeof(CoopCompanionPlan), viewerId), out CoopCompanionPlan? plan)
        && plan is not null
        && plan.RoomName == roomName
        && plan.QuestId == questId
            ? plan
            : null;
}
