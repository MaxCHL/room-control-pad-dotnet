using RoomControl.Core.Models;

namespace RoomControl.Core.Commands;

/// <summary>
/// Backend-to-Pad push published on <c>room/{roomId}/service/notify</c>: order status
/// changes, housekeeping acknowledgements, wake-up call confirmations, billing answers,
/// SOS acknowledgements, etc. Not retained — it is a transient event stream, the Pad is
/// expected to be online (or the notification is re-derivable from FoodOrder/robot state).
/// </summary>
public sealed record ServiceNotification
{
    public required string RoomId { get; init; }
    public required string Category { get; init; } // "Order" | "Housekeeping" | "WakeUpCall" | "Billing" | "Checkout" | "Sos"
    public required string Message { get; init; }
    public string? OrderId { get; init; }
    public string? RobotId { get; init; }
    public OrderStatus? OrderStatus { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
}
