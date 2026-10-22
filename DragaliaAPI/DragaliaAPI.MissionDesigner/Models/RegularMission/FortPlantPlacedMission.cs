namespace DragaliaAPI.MissionDesigner.Models.RegularMission;

public class FortPlantPlacedMission : Mission
{
    public required FortPlants Plant { get; init; }

    protected override MissionCompleteType CompleteType => MissionCompleteType.FortPlantPlaced;

    protected override int? Parameter => (int)this.Plant;

    protected override bool UseTotalValue => true;
}
