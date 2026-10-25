namespace DragaliaAPI.MissionDesigner.Models.RegularMission;

/// <summary>
/// Mission completed by obtaining a specified dragon, including dragons already owned at entry.
/// </summary>
public class DragonEarnedMission : Mission
{
    public required DragonId Dragon { get; init; }

    protected override MissionCompleteType CompleteType => MissionCompleteType.DragonEarned;

    protected override int? Parameter => (int)this.Dragon;
}
