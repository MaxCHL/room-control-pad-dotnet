namespace RoomControl.Core.Models;

/// <summary>
/// Delivery robot status, published (retained) on <c>robot/{robotId}/status</c> so the
/// backend dispatcher (and, indirectly, any Pad tracking an order) always has the latest
/// known state even after reconnecting.
/// </summary>
public sealed record RobotStatusSnapshot
{
    public required string RobotId { get; init; }
    public required RobotState State { get; init; }
    public string? CurrentOrderId { get; init; }
    public string? CurrentRoomId { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
