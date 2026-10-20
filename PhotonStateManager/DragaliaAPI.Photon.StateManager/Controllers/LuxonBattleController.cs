using DragaliaAPI.Photon.Plugin.Plugins.GameLogic;
using DragaliaAPI.Photon.Plugin.Plugins.GameLogic.Events;
using DragaliaAPI.Photon.Shared.Models;
using DragaliaAPI.Photon.StateManager.Authentication;
using DragaliaAPI.Photon.StateManager.Models;
using MessagePack;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DragaliaAPI.Photon.StateManager.Controllers;

[ApiController]
[Route("Luxon")]
[Authorize(AuthenticationSchemes = nameof(PhotonAuthenticationHandler))]
[RequestSizeLimit(1024 * 1024)]
public sealed class LuxonBattleController(LuxonApiClient api, ILogger<LuxonBattleController> logger)
    : ControllerBase
{
    private static readonly MessagePackSerializerOptions WireOptions =
        MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);

    [HttpPost("Prepare")]
    public async Task<IActionResult> Prepare(
        LuxonPrepareRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!ValidActors(request.Actors) || (request.IsSolo && request.Actors.Count != 1))
        {
            return this.BadRequest();
        }

        int questId = request.Actors[0].QuestId;
        // The first battle milestone covers ordinary quests, not raids/BR/rankings.
        if (
            QuestHelper.GetIsRaid(questId)
            || QuestHelper.GetIsRanked(questId)
            || QuestHelper.GetIsRandomMatching(questId)
        )
        {
            return this.UnprocessableEntity();
        }

        try
        {
            List<HeroParamData> heroes = await api.GetHeroes(request.Actors, cancellationToken);
            if (
                heroes.Count != request.Actors.Count
                || heroes.Any(x => x is null)
                || heroes.Select(x => x.ActorNr).Distinct().Count() != heroes.Count
                || heroes.Any(x =>
                    x is null
                    || x.HeroParamLists is null
                    || !request.Actors.Any(a =>
                        a.ActorNr == x.ActorNr
                        && a.ViewerId == x.ViewerId
                        && a.PartySlots.Length == x.HeroParamLists.Count
                    )
                    || x.HeroParamLists.Any(party => party is null || party.Count is < 1 or > 4)
                )
            )
            {
                return this.StatusCode(502);
            }

            heroes = heroes.OrderBy(x => x.ActorNr).ToList();
            Dictionary<int, int> counts = heroes.ToDictionary(
                x => x.ActorNr,
                x => request.IsSolo ? x.HeroParamLists.Min(party => party.Count) : 1
            );
            if (!request.IsSolo)
            {
                for (int pass = 0; pass < 3 && counts.Values.Sum() < 4; pass++)
                {
                    foreach (HeroParamData hero in heroes)
                    {
                        if (counts.Values.Sum() >= 4)
                        {
                            break;
                        }

                        counts[hero.ActorNr] = Math.Min(
                            counts[hero.ActorNr] + 1,
                            hero.HeroParamLists.Min(x => x.Count)
                        );
                    }
                }
            }

            LuxonPreparedBattle result = new()
            {
                MemberCounts = counts,
                Party = MessagePackSerializer.Serialize(
                    new PartyEvent { MemberCountTable = counts, ReBattleCount = 60 },
                    WireOptions,
                    cancellationToken
                ),
            };
            foreach (HeroParamData hero in heroes)
            {
                foreach (List<HeroParam> party in hero.HeroParamLists)
                {
                    result.Characters.Add(
                        MessagePackSerializer.Serialize(
                            new CharacterData
                            {
                                PlayerId = hero.ActorNr,
                                HeroParams = party.Take(counts[hero.ActorNr]).ToArray(),
                                HeroParamExs = party
                                    .Select(x => new HeroParamExData
                                    {
                                        SequenceNumber = x.Position,
                                        LimitOverCount = x.ExAbilityLv,
                                    })
                                    .ToArray(),
                            },
                            WireOptions,
                            cancellationToken
                        )
                    );
                }
            }

            return this.Ok(result);
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or System.Text.Json.JsonException
                        or InvalidOperationException
                        or TaskCanceledException
            )
        {
            // Never log request payloads, bearer tokens or remote exception text.
            logger.LogWarning("Luxon role preparation failed");
            return this.StatusCode(502);
        }
    }

    [HttpPost("Clear")]
    public async Task<IActionResult> Clear(
        LuxonClearRequest request,
        CancellationToken cancellationToken
    )
    {
        if (
            !ValidActors(request.Actors)
            || !request.Actors.Any(x => x.ActorNr == request.ActorNr)
            || request.MemberCount is < 1 or > 4
            || request.Record.Length is < 1 or > 524288
            || request.RoomName.Length is < 1 or > 128
            || request.RoomName.Any(char.IsControl)
            || Math.Abs((long)request.RoomId) is < 1000000 or > 9999999
        )
        {
            return this.BadRequest();
        }

        try
        {
            Dictionary<string, object?> record = MessagePackSerializer.Deserialize<
                Dictionary<string, object?>
            >(
                request.Record,
                MessagePackSerializerOptions.Standard.WithSecurity(
                    MessagePackSecurity.UntrustedData
                ),
                cancellationToken
            );
            if (record is null)
            {
                return this.BadRequest();
            }

            record["connecting_viewer_id_list"] = request
                .Actors.Where(x => x.ActorNr != request.ActorNr)
                .OrderBy(x => x.ActorNr)
                .Select(x => x.ViewerId)
                .ToArray();
            record["is_host"] = request.ActorNr == 1;
            record["member_count"] = request.MemberCount;
            if (request.AstralBetCount is int count)
            {
                record["astral_bet_count"] = count;
            }

            byte[] body = MessagePackSerializer.Serialize(
                record,
                cancellationToken: cancellationToken
            );
            byte[] response = await api.Record(request, body, cancellationToken);
            return this.Ok(new { Response = response });
        }
        catch (MessagePackSerializationException)
        {
            return this.BadRequest();
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or InvalidOperationException
                        or TaskCanceledException
            )
        {
            logger.LogWarning("Luxon quest record failed; request was not retried");
            return this.StatusCode(502);
        }
    }

    private static bool ValidActors(List<ActorInfo> actors) =>
        actors.Count is >= 1 and <= 4
        && actors.Any(x => x.ActorNr == 1)
        && actors.Select(x => x.ActorNr).Distinct().Count() == actors.Count
        && actors.Select(x => x.ViewerId).Distinct().Count() == actors.Count
        && actors.All(x =>
            x.ActorNr > 0
            && x.ViewerId > 0
            && x.QuestId == actors[0].QuestId
            && x.QuestId > 0
            && x.PartySlots is { Length: >= 1 and <= 4 }
            && x.PartySlots.All(slot => slot > 0)
        );
}
