using RoomControl.Core.Commands;
using RoomControl.Core.Models;

namespace RoomControl.Core.Devices;

/// <summary>
/// Abstraction over the physical device layer that sits behind a room gateway. A real
/// deployment would have one concrete driver family per physical bus:
///   - Zigbee coordinator: lights, curtains, and most sensors (mesh join/pairing, radio
///     retries, ZCL clusters such as On/Off, Level Control, Color Control, Window Covering)
///   - DALI gateway: architectural dimming ballasts, addressed independently of Zigbee
///   - HDMI-CEC / IR blaster: TV power, volume, channel, input/cast switching
///   - KNX / Modbus gateway: central VRV/VRF air-conditioning units
/// This simulation deliberately does NOT model radio-level Zigbee behavior (join, retries,
/// mesh routing) — that is firmware-layer concern. It only models the software contract this
/// project is a demo of: "receive an MQTT-carried command, mutate device state as if the
/// hardware moved, report the resulting state back."
/// </summary>
public interface IDeviceDriver
{
    /// <summary>
    /// Applies an incoming <c>.../set</c> command to a device's current state and returns
    /// the resulting state. Throws <see cref="InvalidOperationException"/> if the command
    /// type does not match the device's type (a real driver would reject/NACK instead).
    /// </summary>
    DeviceState Apply(DeviceState current, DeviceCommand command);
}
