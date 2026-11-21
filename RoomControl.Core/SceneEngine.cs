using RoomControl.Core.Commands;
using RoomControl.Core.Models;

namespace RoomControl.Core;

/// <summary>
/// Pure business logic that expands one named <see cref="SceneId"/> into the list of
/// per-device commands needed to realize it in a given room. Producing the command list here
/// (rather than in the gateway) keeps "what a scene means" independent of "how commands get
/// applied under one lock", which is the gateway's job.
/// </summary>
public static class SceneEngine
{
    public static IReadOnlyList<DeviceCommand> BuildSceneCommands(SceneId scene, RoomDeviceInventory inventory, DateTimeOffset issuedAtUtc)
    {
        var commands = new List<DeviceCommand>();

        string NewId() => Guid.NewGuid().ToString("N");

        void SetLight(string zone, bool on, int brightness, int kelvin)
        {
            if (!inventory.LightZoneDeviceIds.TryGetValue(zone, out var deviceId))
            {
                return; // room has no such lighting zone installed
            }

            commands.Add(new LightSetCommand
            {
                CommandId = NewId(),
                RoomId = inventory.RoomId,
                DeviceId = deviceId,
                IssuedAtUtc = issuedAtUtc,
                IsOn = on,
                BrightnessPercent = brightness,
                ColorTemperatureKelvin = kelvin
            });
        }

        void SetAllLights(bool on, int brightness, int kelvin)
        {
            foreach (var zone in inventory.LightZoneDeviceIds.Keys)
            {
                SetLight(zone, on, brightness, kelvin);
            }
        }

        void SetCurtain(CurtainPosition position) => commands.Add(new CurtainSetCommand
        {
            CommandId = NewId(),
            RoomId = inventory.RoomId,
            DeviceId = inventory.CurtainDeviceId,
            IssuedAtUtc = issuedAtUtc,
            Position = position
        });

        void SetAircon(bool on, double? targetC, AirconMode? mode, int? fanSpeed) => commands.Add(new AirconSetCommand
        {
            CommandId = NewId(),
            RoomId = inventory.RoomId,
            DeviceId = inventory.AirconDeviceId,
            IssuedAtUtc = issuedAtUtc,
            IsOn = on,
            TargetTemperatureC = targetC,
            Mode = mode,
            FanSpeed = fanSpeed
        });

        void SetTv(bool on) => commands.Add(new TvSetCommand
        {
            CommandId = NewId(),
            RoomId = inventory.RoomId,
            DeviceId = inventory.TvDeviceId,
            IssuedAtUtc = issuedAtUtc,
            IsOn = on
        });

        void SetDoorSign(DoorSignMode mode) => commands.Add(new DoorSignSetCommand
        {
            CommandId = NewId(),
            RoomId = inventory.RoomId,
            DeviceId = inventory.DoorSignDeviceId,
            IssuedAtUtc = issuedAtUtc,
            Mode = mode
        });

        switch (scene)
        {
            case SceneId.AllOn:
                SetAllLights(on: true, brightness: 100, kelvin: 4000);
                SetCurtain(CurtainPosition.Open);
                break;

            case SceneId.Reading:
                SetAllLights(on: false, brightness: 0, kelvin: 4000);
                SetLight("main", on: true, brightness: 85, kelvin: 4500);
                SetLight("bed", on: true, brightness: 70, kelvin: 4200);
                SetAircon(on: true, targetC: 24.0, mode: AirconMode.Cool, fanSpeed: 2);
                SetTv(on: false);
                break;

            case SceneId.Sleep:
                SetAllLights(on: false, brightness: 0, kelvin: 2700);
                SetCurtain(CurtainPosition.Closed);
                SetAircon(on: true, targetC: 26.0, mode: AirconMode.Auto, fanSpeed: 1);
                SetTv(on: false);
                break;

            case SceneId.NightLight:
                SetAllLights(on: false, brightness: 0, kelvin: 2700);
                SetLight("night", on: true, brightness: 10, kelvin: 2700);
                SetCurtain(CurtainPosition.Closed);
                break;

            case SceneId.Away:
                SetAllLights(on: false, brightness: 0, kelvin: 4000);
                SetTv(on: false);
                SetAircon(on: true, targetC: 28.0, mode: AirconMode.Auto, fanSpeed: 1); // energy-saving setback, not full power-off
                SetCurtain(CurtainPosition.Closed);
                SetDoorSign(DoorSignMode.DoNotDisturb);
                break;

            case SceneId.DoNotDisturb:
                SetDoorSign(DoorSignMode.DoNotDisturb);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scene), scene, "Unknown scene");
        }

        return commands;
    }
}
