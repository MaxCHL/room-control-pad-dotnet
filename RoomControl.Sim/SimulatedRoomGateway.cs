using System.Collections.Concurrent;
using System.Text.Json;
using RoomControl.Core;
using RoomControl.Core.Commands;
using RoomControl.Core.Devices;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;

namespace RoomControl.Sim;

/// <summary>
/// Simulates the in-room gateway box: it is the only thing that talks to
/// <see cref="IDeviceDriver"/>, and the only thing that publishes device/env/gateway state.
/// It owns command reliability: application-level idempotency (dedup by DeviceId + CommandId), ACKs,
/// retained state, and Last Will registration for offline detection.
/// </summary>
public sealed class SimulatedRoomGateway
{
    private readonly string _clientId;
    private readonly IRoomMessageBus _bus;
    private readonly IDeviceDriver _driver;
    private readonly RoomDeviceInventory _inventory;
    private readonly ConcurrentDictionary<string, DeviceState> _deviceStates = new();
    // Idempotency caches. Device commands are keyed by (DeviceId, CommandId) so a CommandId reused
    // on another device is never answered with the wrong cached ACK; scenes have their own cache.
    private readonly ConcurrentDictionary<(string DeviceId, string CommandId), CommandAck> _processedCommands = new();
    private readonly ConcurrentDictionary<string, CommandAck> _processedScenes = new();

    // Serializes a scene's device commands (no other command interleaves) and concurrent
    // single-device commands against it. This is not a transaction: a mid-scene device
    // failure does not roll back devices that were already applied.
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    public string RoomId { get; }

    public SimulatedRoomGateway(RoomDeviceInventory inventory, IRoomMessageBus bus, IDeviceDriver driver)
    {
        RoomId = inventory.RoomId;
        _inventory = inventory;
        _bus = bus;
        _driver = driver;
        _clientId = $"gateway-{RoomId}";
    }

    /// <summary>Boots the gateway: seeds default device/env state, publishes them retained,
    /// registers the offline Last Will, and subscribes to incoming commands.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        // Connect() clears any will left over from a previous session, so it must run before
        // this session's will is registered — otherwise it would immediately wipe it out.
        _bus.Connect(_clientId);
        _bus.RegisterLastWill(_clientId, RoomTopics.GatewayStatus(RoomId), GatewayStatusPayload(online: false), retain: true);

        SeedInitialDeviceStates();
        foreach (var state in _deviceStates.Values)
        {
            await PublishStateAsync(state, ct).ConfigureAwait(false);
        }

        await _bus.PublishAsync(RoomTopics.GatewayStatus(RoomId), GatewayStatusPayload(online: true), retain: true, ct).ConfigureAwait(false);

        await _bus.SubscribeAsync(RoomTopics.AllDeviceSetsInRoom(RoomId), OnDeviceSetAsync, ct).ConfigureAwait(false);
        await _bus.SubscribeAsync(RoomTopics.SceneSet(RoomId), OnSceneSetAsync, ct).ConfigureAwait(false);
    }

    /// <summary>Simulates the gateway losing power/network without a clean disconnect,
    /// causing its Last Will to fire so any Pad watching gateway/status sees it go offline.</summary>
    public Task SimulateCrashAsync(CancellationToken ct = default) => _bus.DisconnectUngracefullyAsync(_clientId, ct);

    public IReadOnlyDictionary<string, DeviceState> CurrentDeviceStates => _deviceStates;

    private void SeedInitialDeviceStates()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (zone, deviceId) in _inventory.LightZoneDeviceIds)
        {
            _deviceStates[deviceId] = new LightState
            {
                RoomId = RoomId,
                DeviceId = deviceId,
                Zone = zone,
                IsOn = false,
                BrightnessPercent = 0,
                ColorTemperatureKelvin = 4000,
                UpdatedAtUtc = now
            };
        }

        _deviceStates[_inventory.CurtainDeviceId] = new CurtainState
        {
            RoomId = RoomId,
            DeviceId = _inventory.CurtainDeviceId,
            IsOn = false,
            Position = CurtainPosition.Closed,
            UpdatedAtUtc = now
        };

        _deviceStates[_inventory.AirconDeviceId] = new AirconState
        {
            RoomId = RoomId,
            DeviceId = _inventory.AirconDeviceId,
            IsOn = false,
            TargetTemperatureC = 24.0,
            Mode = AirconMode.Auto,
            FanSpeed = 2,
            UpdatedAtUtc = now
        };

        _deviceStates[_inventory.TvDeviceId] = new TvState
        {
            RoomId = RoomId,
            DeviceId = _inventory.TvDeviceId,
            IsOn = false,
            Volume = 20,
            Channel = 1,
            UpdatedAtUtc = now
        };

        _deviceStates[_inventory.DoorSignDeviceId] = new DoorSignState
        {
            RoomId = RoomId,
            DeviceId = _inventory.DoorSignDeviceId,
            IsOn = false,
            Mode = DoorSignMode.None,
            UpdatedAtUtc = now
        };
    }

    private async Task OnDeviceSetAsync(string topic, string payload, CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<DeviceCommand>(payload)
            ?? throw new InvalidOperationException($"Malformed device command on '{topic}'.");

        await _applyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ack = ApplyDeviceCommandLocked(command);
            // Unknown devices have no state to publish; still send the failure ACK below.
            if (_deviceStates.TryGetValue(command.DeviceId, out var state))
            {
                await PublishStateAsync(state, ct).ConfigureAwait(false);
            }
            await _bus.PublishAsync(RoomTopics.DeviceSetAck(RoomId, command.DeviceId), JsonSerializer.Serialize(ack), retain: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task OnSceneSetAsync(string topic, string payload, CancellationToken ct)
    {
        var sceneCommand = JsonSerializer.Deserialize<SceneSetCommand>(payload)
            ?? throw new InvalidOperationException($"Malformed scene command on '{topic}'.");

        await _applyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_processedScenes.TryGetValue(sceneCommand.CommandId, out var existingAck))
            {
                await _bus.PublishAsync(RoomTopics.SceneSetAck(RoomId), JsonSerializer.Serialize(existingAck with { WasDuplicate = true }), retain: false, ct).ConfigureAwait(false);
                return;
            }

            // Serialization: every device command that makes up the scene is applied while still
            // holding _applyLock, so no interleaved single-device command can observe a
            // partially-applied scene. A failure part-way is not rolled back.
            var deviceCommands = SceneEngine.BuildSceneCommands(sceneCommand.Scene, _inventory, sceneCommand.IssuedAtUtc);
            var deviceAcks = new List<CommandAck>(deviceCommands.Count);
            foreach (var deviceCommand in deviceCommands)
            {
                var deviceAck = ApplyDeviceCommandLocked(deviceCommand);
                deviceAcks.Add(deviceAck);
                // A device missing from the gateway yields a failed device ACK and has no state to publish.
                if (_deviceStates.TryGetValue(deviceCommand.DeviceId, out var deviceState))
                {
                    await PublishStateAsync(deviceState, ct).ConfigureAwait(false);
                }
            }

            var sceneAck = new CommandAck
            {
                CommandId = sceneCommand.CommandId,
                RoomId = RoomId,
                Success = deviceAcks.All(a => a.Success),
                WasDuplicate = false,
                Message = $"Scene '{sceneCommand.Scene}' applied {deviceAcks.Count} device command(s).",
                AckedAtUtc = DateTimeOffset.UtcNow
            };
            _processedScenes[sceneCommand.CommandId] = sceneAck;
            await _bus.PublishAsync(RoomTopics.SceneSetAck(RoomId), JsonSerializer.Serialize(sceneAck), retain: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private CommandAck ApplyDeviceCommandLocked(DeviceCommand command)
    {
        var now = DateTimeOffset.UtcNow;

        if (_processedCommands.TryGetValue((command.DeviceId, command.CommandId), out var existingAck))
        {
            return existingAck with { WasDuplicate = true, AckedAtUtc = now };
        }

        if (!_deviceStates.TryGetValue(command.DeviceId, out var current))
        {
            var notFoundAck = new CommandAck
            {
                CommandId = command.CommandId,
                RoomId = RoomId,
                Success = false,
                WasDuplicate = false,
                Message = $"Unknown device '{command.DeviceId}'.",
                AckedAtUtc = now
            };
            _processedCommands[(command.DeviceId, command.CommandId)] = notFoundAck;
            return notFoundAck;
        }

        var next = _driver.Apply(current, command);
        _deviceStates[command.DeviceId] = next;

        var ack = new CommandAck
        {
            CommandId = command.CommandId,
            RoomId = RoomId,
            Success = true,
            WasDuplicate = false,
            AckedAtUtc = now
        };
        _processedCommands[(command.DeviceId, command.CommandId)] = ack;
        return ack;
    }

    private Task PublishStateAsync(DeviceState state, CancellationToken ct) =>
        _bus.PublishAsync(RoomTopics.DeviceState(RoomId, state.DeviceId), JsonSerializer.Serialize(state), retain: true, ct);

    private static string GatewayStatusPayload(bool online) =>
        JsonSerializer.Serialize(new { Online = online, AtUtc = DateTimeOffset.UtcNow });
}
