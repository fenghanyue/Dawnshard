using DragaliaAPI.Photon.Shared.Enums;
using DragaliaAPI.Photon.Shared.Models;
using DragaliaAPI.Photon.StateManager.Models;
using Microsoft.AspNetCore.Mvc;

namespace DragaliaAPI.Photon.StateManager.Controllers;

public partial class EventController
{
    /// <summary>
    /// Replace a Luxon room snapshot. Repeating a request never adds duplicate players.
    /// The plugin renews this short lease every 15 seconds; a crashed server leaves no
    /// searchable room after 60 seconds. Legacy Photon webhooks retain their existing TTL.
    /// </summary>
    [HttpPost("LuxonSnapshot")]
    [RequestSizeLimit(65_536)]
    public async Task<IActionResult> LuxonSnapshot(LuxonSnapshotRequest request)
    {
        GameBase game = request.Game;
        if (
            string.IsNullOrWhiteSpace(game.Name)
            || game.Name.Length > 128
            || game.RoomId is < 1_000_000 or > 9_999_999
            || game.QuestId <= 0
            || game.MatchingCompatibleId < 0
            || !Enum.IsDefined(game.MatchingType)
            || game.MatchingType == MatchingTypes.Undefined
            || game.Players.Count is < 1 or > 4
            || game.Players.Count(x => x.ActorNr == 1) != 1
            || game.Players.Any(x => x.ActorNr <= 0 || x.ViewerId <= 0)
            || game.Players.Select(x => x.ActorNr).Distinct().Count() != game.Players.Count
            || game.Players.Select(x => x.ViewerId).Distinct().Count() != game.Players.Count
        )
        {
            return this.BadRequest();
        }

        RedisGame snapshot = new(game);
        await this.Games.InsertAsync(snapshot, TimeSpan.FromSeconds(60));
        return this.Ok();
    }
}
