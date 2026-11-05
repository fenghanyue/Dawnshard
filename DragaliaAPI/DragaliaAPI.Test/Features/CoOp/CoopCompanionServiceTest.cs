using DragaliaAPI.Features.CoOp;
using DragaliaAPI.Models.Generated;
using DragaliaAPI.Photon.Shared.Models;
using DragaliaAPI.Shared.Definitions.Enums;
using DragaliaAPI.Shared.PlayerDetails;
using Microsoft.Extensions.Logging.Abstractions;

namespace DragaliaAPI.Test.Features.CoOp;

public class CoopCompanionServiceTest
{
    private readonly Mock<IPhotonStateApi> state = new(MockBehavior.Strict);
    private readonly Mock<IPlayerIdentityService> identity = new(MockBehavior.Strict);
    private readonly CoopCompanionService service;

    public CoopCompanionServiceTest()
    {
        identity.SetupGet(x => x.ViewerId).Returns(123);
        service = new(state.Object, identity.Object, NullLogger<CoopCompanionService>.Instance);
    }

    [Fact]
    public async Task ApplyHp_UsesPreparedCompanionsAcrossPartiesAndProtectsLeaders()
    {
        IngameData data = Data();
        ApiGame game = Game();
        game.CompanionPlan.Units =
        [
            Companion(2),
            Companion(6),
            Companion(1),
            new()
            {
                Position = 3,
                CharacterId = 999,
                Hp = 99999,
            },
        ];
        state.Setup(x => x.GetGameByViewerId(123)).ReturnsAsync(game);

        await service.ApplyHp(data);

        foreach (PartyUnitList unit in data.PartyInfo.PartyUnitList)
        {
            unit.CharaData.Hp.Should().Be(unit.Position is 2 or 6 ? 99999 : 1000 + unit.Position);
            unit.CharaData.Attack.Should().Be(2000 + unit.Position);
        }

        state.VerifyAll();
    }

    [Theory]
    [InlineData("missing-room")]
    [InlineData("missing-plan")]
    [InlineData("other-room")]
    [InlineData("other-quest")]
    [InlineData("other-player")]
    [InlineData("no-companions")]
    [InlineData("solo")]
    public async Task ApplyHp_PreservesStatsOutsideMatchingPreparedBattle(string scenario)
    {
        IngameData data = Data();
        ApiGame? game = Game();
        switch (scenario)
        {
            case "missing-room":
                game = null;
                break;
            case "missing-plan":
                game.CompanionPlan = null!;
                break;
            case "other-room":
                game.CompanionPlan.RoomName = "another-room";
                break;
            case "other-quest":
                game.CompanionPlan.QuestId++;
                break;
            case "other-player":
                game.Players[0].ViewerId = 456;
                break;
            case "no-companions":
                game.CompanionPlan.Units.Clear();
                break;
            case "solo":
                data.PlayType = QuestPlayType.Default;
                break;
        }

        if (scenario != "solo")
        {
            state.Setup(x => x.GetGameByViewerId(123)).ReturnsAsync(game);
        }

        await service.ApplyHp(data);

        data.PartyInfo.PartyUnitList.Should().OnlyContain(x => x.CharaData.Hp == 1000 + x.Position);
        state.VerifyAll();
    }

    private static CoopCompanionUnit Companion(int position) =>
        new()
        {
            Position = position,
            CharacterId = 10100100 + position,
            Hp = 99999,
        };

    private static ApiGame Game() =>
        new()
        {
            Name = "prepared-room",
            QuestId = 301010103,
            Players = [new() { ActorNr = 1, ViewerId = 123 }],
            CompanionPlan = new()
            {
                RoomName = "prepared-room",
                QuestId = 301010103,
                Units = [Companion(2)],
            },
        };

    private static IngameData Data() =>
        new()
        {
            QuestId = 301010103,
            PlayType = QuestPlayType.Multi,
            PartyInfo = new()
            {
                PartyUnitList = Enumerable
                    .Range(1, 8)
                    .Select(position => new PartyUnitList
                    {
                        Position = position,
                        CharaData = new()
                        {
                            CharaId = (Charas)(10100100 + position),
                            Hp = 1000 + position,
                            Attack = 2000 + position,
                        },
                    })
                    .ToList(),
            },
        };
}
