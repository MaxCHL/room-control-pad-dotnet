using System.Text.Json.Serialization;
using RoomControl.Core.Models;

namespace RoomControl.Core.Commands;

/// <summary>
/// Everything a guest can request from the Pad that is handled by a backend service rather
/// than a physical device, published on the shared <c>room/{roomId}/service/order</c>
/// channel. One topic, many request kinds — the backend routes by "$type", the same way a
/// real hotel PMS/service-desk integration multiplexes guest requests over one queue.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FoodOrderRequest), "foodOrder")]
[JsonDerivedType(typeof(ConfirmPickupRequest), "confirmPickup")]
[JsonDerivedType(typeof(HousekeepingRequest), "housekeeping")]
public abstract record GuestServiceRequest
{
    public required string RequestId { get; init; }
    public required string RoomId { get; init; }
    public required DateTimeOffset RequestedAtUtc { get; init; }
}

/// <summary>Guest places an in-room dining order; handled by <c>RobotDispatchService</c>.</summary>
public sealed record FoodOrderRequest : GuestServiceRequest
{
    public required IReadOnlyList<FoodOrderItem> Items { get; init; }
}

/// <summary>Guest confirms they collected the food from the robot at the door.</summary>
public sealed record ConfirmPickupRequest : GuestServiceRequest
{
    public required string OrderId { get; init; }
}

/// <summary>Non-food guest service request; handled by <c>GuestServicesService</c>.</summary>
public sealed record HousekeepingRequest : GuestServiceRequest
{
    public required GuestRequestKind Kind { get; init; }
    public string? Details { get; init; }
}
