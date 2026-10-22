namespace DragaliaAPI.MissionDesigner.Models.EventMission;

public class EventChallengeBattleClearTimeMission : Mission
{
    public int EventId { get; set; }

    public required int QuestId { get; init; }

    public required int MaxSeconds { get; init; }

    protected override MissionCompleteType CompleteType =>
        MissionCompleteType.EventChallengeBattleClearTime;

    protected override int? Parameter => this.EventId;

    protected override int? Parameter2 => this.QuestId;

    protected override int? Parameter3 => this.MaxSeconds;

    protected override bool UseTotalValue => true;
}
