using System.Text.Json.Serialization;

namespace RoomControl.Core.Models;

/// <summary>
/// Base state of one in-room device, as published (retained) on
/// <c>room/{roomId}/device/{deviceId}/state</c>. Serialized polymorphically so the wire
/// payload carries a "$type" discriminator, matching how a real MQTT JSON payload would
/// distinguish device kinds.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(LightState), "light")]
[JsonDerivedType(typeof(CurtainState), "curtain")]
[JsonDerivedType(typeof(AirconState), "aircon")]
[JsonDerivedType(typeof(TvState), "tv")]
[JsonDerivedType(typeof(DoorSignState), "doorSign")]
public abstract record DeviceState
{
    public required string RoomId { get; init; }
    public required string DeviceId { get; init; }
    public abstract DeviceType Type { get; }

    /// <summary>Whether the device reports itself as powered/active. Meaning is device-specific
    /// (e.g. for DoorSign it means "a sign is currently set"; for Curtain it is informational only,
    /// the authoritative field is <see cref="CurtainState.Position"/>).</summary>
    public required bool IsOn { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record LightState : DeviceState
{
    public override DeviceType Type => DeviceType.Light;

    /// <summary>Logical lighting zone within the room, e.g. "main", "bed", "bath", "night".</summary>
    public required string Zone { get; init; }

    public required int BrightnessPercent { get; init; } // 0-100
    public required int ColorTemperatureKelvin { get; init; } // 2700 (warm) - 6500 (daylight)
}

public sealed record CurtainState : DeviceState
{
    public override DeviceType Type => DeviceType.Curtain;
    public required CurtainPosition Position { get; init; }
}

public sealed record AirconState : DeviceState
{
    public override DeviceType Type => DeviceType.Aircon;
    public required double TargetTemperatureC { get; init; }
    public required AirconMode Mode { get; init; }
    public required int FanSpeed { get; init; } // 1 (quiet) - 5 (max)
}

public sealed record TvState : DeviceState
{
    public override DeviceType Type => DeviceType.Tv;
    public required int Volume { get; init; } // 0-100
    public required int Channel { get; init; }

    /// <summary>Non-null when the guest is casting from a personal device (Chromecast/AirPlay style).</summary>
    public string? CastSource { get; init; }
}

public sealed record DoorSignState : DeviceState
{
    public override DeviceType Type => DeviceType.DoorSign;
    public required DoorSignMode Mode { get; init; }
}
