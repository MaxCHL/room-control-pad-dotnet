using System.Text.Json.Serialization;
using RoomControl.Core.Models;

namespace RoomControl.Core.Commands;

/// <summary>
/// Base type for an application-level command published to
/// <c>room/{roomId}/device/{deviceId}/set</c>. <see cref="CommandId"/> is the idempotency
/// key: a QoS1-style redelivery (same CommandId seen twice) must be a no-op on the second
/// application, which <c>SimulatedRoomGateway</c> enforces.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(LightSetCommand), "light")]
[JsonDerivedType(typeof(CurtainSetCommand), "curtain")]
[JsonDerivedType(typeof(AirconSetCommand), "aircon")]
[JsonDerivedType(typeof(TvSetCommand), "tv")]
[JsonDerivedType(typeof(DoorSignSetCommand), "doorSign")]
public abstract record DeviceCommand
{
    public required string CommandId { get; init; }
    public required string RoomId { get; init; }
    public required string DeviceId { get; init; }
    public required DateTimeOffset IssuedAtUtc { get; init; }
}

/// <summary>Every field is optional: only the fields the guest actually changed are sent,
/// the rest of the device state is left untouched.</summary>
public sealed record LightSetCommand : DeviceCommand
{
    public bool? IsOn { get; init; }
    public int? BrightnessPercent { get; init; }
    public int? ColorTemperatureKelvin { get; init; }
}

public sealed record CurtainSetCommand : DeviceCommand
{
    public required CurtainPosition Position { get; init; }
}

public sealed record AirconSetCommand : DeviceCommand
{
    public bool? IsOn { get; init; }
    public double? TargetTemperatureC { get; init; }
    public AirconMode? Mode { get; init; }
    public int? FanSpeed { get; init; }
}

public sealed record TvSetCommand : DeviceCommand
{
    public bool? IsOn { get; init; }
    public int? Volume { get; init; }
    public int? Channel { get; init; }
    public string? CastSource { get; init; }
}

public sealed record DoorSignSetCommand : DeviceCommand
{
    public required DoorSignMode Mode { get; init; }
}
