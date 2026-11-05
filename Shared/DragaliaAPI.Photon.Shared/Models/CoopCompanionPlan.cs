namespace DragaliaAPI.Photon.Shared.Models;

/// <summary>The temporary companion stats selected for one player's current co-op battle.</summary>
public sealed class CoopCompanionPlan
{
    public string RoomName { get; set; } = "";

    public int QuestId { get; set; }

    public List<CoopCompanionUnit> Units { get; set; } = new();
}

public sealed class CoopCompanionUnit
{
    public int Position { get; set; }

    public int CharacterId { get; set; }

    public int Hp { get; set; }
}
