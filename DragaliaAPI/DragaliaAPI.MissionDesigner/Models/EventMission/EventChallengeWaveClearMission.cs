namespace DragaliaAPI.MissionDesigner.Models.EventMission;

public class EventChallengeWaveClearMission : Mission
{
    public int EventId { get; set; }

    public required int QuestId { get; init; }

    protected override MissionCompleteType CompleteType =>
        MissionCompleteType.EventChallengeWaveClear;

    protected override int? Parameter => this.EventId;

    protected override int? Parameter2 => this.QuestId;
}
