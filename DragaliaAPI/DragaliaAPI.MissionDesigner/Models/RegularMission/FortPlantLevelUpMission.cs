namespace DragaliaAPI.MissionDesigner.Models.RegularMission;

public class FortPlantLevelUpMission : Mission
{
    public required FortPlants Plant { get; init; }

    protected override MissionCompleteType CompleteType => MissionCompleteType.FortPlantLevelUp;

    protected override int? Parameter => (int)this.Plant;

    protected override bool UseTotalValue => true;
}
