using RoomControl.Core.Models;

namespace RoomControl.Core.Commands;

/// <summary>
/// Published to <c>room/{roomId}/scene/set</c>. A scene expands into multiple
/// <see cref="DeviceCommand"/>s (see <see cref="RoomControl.Core.SceneEngine"/>) that must
/// be applied by the gateway under a single lock (serialized, not all-or-nothing).
/// </summary>
public sealed record SceneSetCommand
{
    public required string CommandId { get; init; }
    public required string RoomId { get; init; }
    public required SceneId Scene { get; init; }
    public required DateTimeOffset IssuedAtUtc { get; init; }
}
