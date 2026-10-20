using DragaliaAPI.Photon.Shared.Models;

namespace DragaliaAPI.Photon.StateManager.Models;

public sealed class LuxonApiOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;
}

public sealed class LuxonPrepareRequest
{
    public List<ActorInfo> Actors { get; init; } = [];

    public bool IsSolo { get; init; }
}

public sealed class LuxonPreparedBattle
{
    public Dictionary<int, int> MemberCounts { get; init; } = [];

    public byte[] Party { get; init; } = [];

    public List<byte[]> Characters { get; init; } = [];
}

public sealed class LuxonClearRequest
{
    public List<ActorInfo> Actors { get; init; } = [];

    public int ActorNr { get; init; }

    public int MemberCount { get; init; }

    public int? AstralBetCount { get; init; }

    public string RoomName { get; init; } = string.Empty;

    public int RoomId { get; init; }

    public byte[] Record { get; init; } = [];
}
