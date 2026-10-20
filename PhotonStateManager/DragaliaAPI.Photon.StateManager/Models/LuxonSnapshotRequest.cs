using System.ComponentModel.DataAnnotations;
using DragaliaAPI.Photon.Shared.Models;

namespace DragaliaAPI.Photon.StateManager.Models;

/// <summary>A complete, retryable snapshot from the local Luxon room plugin.</summary>
public sealed class LuxonSnapshotRequest
{
    [Required]
    public required GameBase Game { get; init; }
}
