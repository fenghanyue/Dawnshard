using DragaliaAPI.Models.Generated;
using DragaliaAPI.Photon.Shared.Models;
using DragaliaAPI.Shared.Definitions.Enums;
using DragaliaAPI.Shared.PlayerDetails;

namespace DragaliaAPI.Features.CoOp;

public sealed partial class CoopCompanionService(
    IPhotonStateApi photonStateApi,
    IPlayerIdentityService playerIdentityService,
    ILogger<CoopCompanionService> logger
)
{
    public async Task ApplyHp(IngameData data)
    {
        if (data.PlayType != QuestPlayType.Multi)
        {
            return;
        }

        long viewerId = playerIdentityService.ViewerId;
        ApiGame? game = await photonStateApi.GetGameByViewerId(viewerId);
        CoopCompanionPlan? plan = game?.CompanionPlan;
        if (
            plan is null
            || plan.RoomName != game!.Name
            || plan.QuestId != data.QuestId
            || game.QuestId != data.QuestId
            || !game.Players.Any(x => x.ViewerId == viewerId)
        )
        {
            return;
        }

        List<PartyUnitList> units = data
            .PartyInfo.PartyUnitList.Where(x =>
                x.Position > 0 && x.CharaData is { CharaId: not Charas.Empty }
            )
            .OrderBy(x => x.Position)
            .ToList();
        HashSet<int> leaders = units
            .GroupBy(x => (x.Position - 1) / 4)
            .Select(x => x.First().Position)
            .ToHashSet();
        foreach (CoopCompanionUnit companion in plan.Units)
        {
            PartyUnitList? unit = units.FirstOrDefault(x =>
                x.Position == companion.Position
                && (int)x.CharaData.CharaId == companion.CharacterId
            );
            if (unit is null || leaders.Contains(unit.Position) || companion.Hp <= 0)
            {
                continue;
            }

            unit.CharaData.Hp = companion.Hp;
            Log.AppliedCompanionHp(logger, data.QuestId, unit.Position, companion.Hp);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            LogLevel.Information,
            "Co-op companion HP applied: quest={questId} position={position} hp={hp}"
        )]
        public static partial void AppliedCompanionHp(
            ILogger logger,
            int questId,
            int position,
            int hp
        );
    }
}
