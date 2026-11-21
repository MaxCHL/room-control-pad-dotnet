using RoomControl.Core.Commands;
using RoomControl.Core.Devices;
using RoomControl.Core.Models;

namespace RoomControl.Sim;

/// <summary>
/// In-memory stand-in for the real device layer. Each branch below is annotated with the
/// physical bus/protocol a production driver would use for that device family — this class
/// only mutates an in-memory record, it never touches a radio or a bus.
/// </summary>
public sealed class SimulatedDeviceDriver : IDeviceDriver
{
    public DeviceState Apply(DeviceState current, DeviceCommand command) => (current, command) switch
    {
        // Real hardware: Zigbee ZCL "On/Off", "Level Control", "Color Control" clusters.
        (LightState light, LightSetCommand cmd) => light with
        {
            IsOn = cmd.IsOn ?? light.IsOn,
            BrightnessPercent = cmd.BrightnessPercent ?? light.BrightnessPercent,
            ColorTemperatureKelvin = cmd.ColorTemperatureKelvin ?? light.ColorTemperatureKelvin,
            UpdatedAtUtc = cmd.IssuedAtUtc
        },

        // Real hardware: Zigbee ZCL "Window Covering" cluster (or a DALI-driven track motor).
        (CurtainState curtain, CurtainSetCommand cmd) => curtain with
        {
            IsOn = cmd.Position != CurtainPosition.Closed,
            Position = cmd.Position,
            UpdatedAtUtc = cmd.IssuedAtUtc
        },

        // Real hardware: KNX or Modbus write to the room's VRV/VRF fan-coil controller.
        (AirconState aircon, AirconSetCommand cmd) => aircon with
        {
            IsOn = cmd.IsOn ?? aircon.IsOn,
            TargetTemperatureC = cmd.TargetTemperatureC ?? aircon.TargetTemperatureC,
            Mode = cmd.Mode ?? aircon.Mode,
            FanSpeed = cmd.FanSpeed ?? aircon.FanSpeed,
            UpdatedAtUtc = cmd.IssuedAtUtc
        },

        // Real hardware: HDMI-CEC command to the panel, or an IR blaster code for legacy TVs.
        (TvState tv, TvSetCommand cmd) => tv with
        {
            IsOn = cmd.IsOn ?? tv.IsOn,
            Volume = cmd.Volume ?? tv.Volume,
            Channel = cmd.Channel ?? tv.Channel,
            CastSource = cmd.CastSource ?? tv.CastSource,
            UpdatedAtUtc = cmd.IssuedAtUtc
        },

        // Real hardware: simple digital I/O or a small e-paper sign panel outside the door —
        // no field bus needed, it is local to the gateway.
        (DoorSignState sign, DoorSignSetCommand cmd) => sign with
        {
            IsOn = cmd.Mode != DoorSignMode.None,
            Mode = cmd.Mode,
            UpdatedAtUtc = cmd.IssuedAtUtc
        },

        _ => throw new InvalidOperationException(
            $"Device/command type mismatch: device '{current.DeviceId}' is {current.Type} but received a {command.GetType().Name}.")
    };
}
