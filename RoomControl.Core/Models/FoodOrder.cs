namespace RoomControl.Core.Models;

public sealed record FoodOrderItem
{
    public required string Name { get; init; }
    public required int Quantity { get; init; }
}

/// <summary>
/// Full lifecycle record of an in-room dining order, tracked by <c>RobotDispatchService</c>
/// on the backend and mirrored to the Pad via <c>room/{roomId}/service/notify</c> events.
/// </summary>
public sealed record FoodOrder
{
    public required string OrderId { get; init; }
    public required string RoomId { get; init; }
    public required IReadOnlyList<FoodOrderItem> Items { get; init; }
    public required OrderStatus Status { get; init; }
    public string? AssignedRobotId { get; init; }
    public required DateTimeOffset PlacedAtUtc { get; init; }
    public DateTimeOffset? UpdatedAtUtc { get; init; }
}
