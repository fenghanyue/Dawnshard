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
        CoopCompanionPlan plan = Plan();
        plan.Units =
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
        state.Setup(x => x.GetCompanionPlan(123, data.QuestId)).ReturnsAsync(plan);

        await service.ApplyHp(data);

        foreach (PartyUnitList unit in data.PartyInfo.PartyUnitList)
        {
            unit.CharaData.Hp.Should().Be(unit.Position is 2 or 6 ? 99999 : 1000 + unit.Position);
            unit.CharaData.Attack.Should().Be(2000 + unit.Position);
        }

        state.VerifyAll();
    }

    [Theory]
    [InlineData("invalid-hp")]
    [InlineData("missing-plan")]
    [InlineData("empty-room")]
    [InlineData("other-quest")]
    [InlineData("other-player")]
    [InlineData("no-companions")]
    [InlineData("solo")]
    public async Task ApplyHp_PreservesStatsOutsideMatchingPreparedBattle(string scenario)
    {
        IngameData data = Data();
        CoopCompanionPlan? plan = Plan();
        switch (scenario)
        {
            case "invalid-hp":
                plan.Units[0].Hp = 0;
                break;
            case "missing-plan":
                plan = null;
                break;
            case "empty-room":
                plan.RoomName = "";
                break;
            case "other-quest":
                plan.QuestId++;
                break;
            case "other-player":
                plan.ViewerId = 456;
                break;
            case "no-companions":
                plan.Units.Clear();
                break;
            case "solo":
                data.PlayType = QuestPlayType.Default;
                break;
        }

        if (scenario != "solo")
        {
            state.Setup(x => x.GetCompanionPlan(123, data.QuestId)).ReturnsAsync(plan);
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

    private static CoopCompanionPlan Plan() =>
        new()
        {
            RoomName = "prepared-room",
            QuestId = 301010103,
            ViewerId = 123,
            Units = [Companion(2)],
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
