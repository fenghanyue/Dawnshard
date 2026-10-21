namespace DragaliaAPI.MissionDesigner.Models.EventMission;

public class EventItemCollectionMission : Mission
{
    public required int ItemId { get; init; }

    protected override MissionCompleteType CompleteType => MissionCompleteType.EventItemCollection;

    protected override int? Parameter => this.ItemId;
}
