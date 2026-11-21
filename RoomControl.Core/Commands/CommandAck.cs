namespace RoomControl.Core.Commands;

/// <summary>
/// Application-level acknowledgement published back by the gateway after processing a
/// <see cref="DeviceCommand"/> or <see cref="SceneSetCommand"/>. This is what closes the
/// QoS1-plus-idempotency loop: the Pad only considers a command "confirmed" once the ACK
/// with the matching CommandId arrives (retained device state is the other confirmation
/// signal, but the ACK also carries duplicate/error information).
/// </summary>
public sealed record CommandAck
{
    public required string CommandId { get; init; }
    public required string RoomId { get; init; }
    public required bool Success { get; init; }

    /// <summary>True when this CommandId had already been processed before (duplicate
    /// redelivery) and the gateway short-circuited without re-applying the command.</summary>
    public required bool WasDuplicate { get; init; }

    public string? Message { get; init; }
    public required DateTimeOffset AckedAtUtc { get; init; }
}
