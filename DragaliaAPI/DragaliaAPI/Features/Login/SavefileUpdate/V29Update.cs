using DragaliaAPI.Database;
using DragaliaAPI.Database.Entities;
using DragaliaAPI.Shared.PlayerDetails;
using Microsoft.EntityFrameworkCore;

namespace DragaliaAPI.Features.Login.SavefileUpdate;

/// <summary>
/// Retires the 20458 compendium trial without deleting rewards, progress, or claimed flags.
/// </summary>
public class V29Update(
    ApiContext apiContext,
    TimeProvider timeProvider,
    IPlayerIdentityService playerIdentityService
) : ISavefileUpdate
{
    public int SavefileVersion => 29;

    public async Task Apply()
    {
        DbPlayerUserData userData = await apiContext.PlayerUserData.SingleAsync();
        if (userData.ActiveMemoryEventId == 20458)
        {
            userData.ActiveMemoryEventId = 0;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        List<DbPlayerMission> missions = await apiContext
            .PlayerMissions.Where(x =>
                x.ViewerId == playerIdentityService.ViewerId
                && x.GroupId == 20458
                && (x.End == DateTimeOffset.UnixEpoch || x.End > now)
            )
            .ToListAsync();
        foreach (DbPlayerMission mission in missions)
        {
            mission.End = now;
        }
    }
}
