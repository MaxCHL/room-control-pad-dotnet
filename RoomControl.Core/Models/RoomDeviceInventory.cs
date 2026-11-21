namespace RoomControl.Core.Models;

/// <summary>
/// The as-provisioned set of device IDs installed in one room. A real gateway would read
/// this from a provisioning/commissioning record; here it is passed in when the room is
/// simulated. <see cref="RoomControl.Core.SceneEngine"/> uses it to know which concrete
/// devices a named scene should target.
/// </summary>
public sealed record RoomDeviceInventory
{
    public required string RoomId { get; init; }

    /// <summary>Lighting zone name (e.g. "main", "bed", "bath", "night") to device ID.</summary>
    public required IReadOnlyDictionary<string, string> LightZoneDeviceIds { get; init; }

    public required string CurtainDeviceId { get; init; }
    public required string AirconDeviceId { get; init; }
    public required string TvDeviceId { get; init; }
    public required string DoorSignDeviceId { get; init; }

    public IEnumerable<string> AllDeviceIds =>
        LightZoneDeviceIds.Values.Append(CurtainDeviceId).Append(AirconDeviceId).Append(TvDeviceId).Append(DoorSignDeviceId);
}
