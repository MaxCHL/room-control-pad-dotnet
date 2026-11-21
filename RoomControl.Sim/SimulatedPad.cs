using System.Collections.Concurrent;
using System.Text.Json;
using RoomControl.Core.Commands;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;

namespace RoomControl.Sim;

/// <summary>
/// Simulates the in-room control Pad: the guest-facing client. It caches every retained
/// topic it subscribes to (device state, room env, gateway status, watched facilities) so
/// its "screen" can be printed at any time, and it issues commands/requests with an explicit
/// CommandId so the caller can deliberately resend one to demonstrate idempotency.
/// </summary>
public sealed class SimulatedPad
{
    private readonly string _roomId;
    private readonly IRoomMessageBus _bus;

    private readonly ConcurrentDictionary<string, DeviceState> _deviceCache = new();
    private RoomEnvState? _envCache;
    private bool _gatewayOnline;
    private readonly ConcurrentDictionary<string, FacilityOccupancyState> _facilityCache = new();

    /// <summary>Raised whenever a backend push notification arrives, so the demo script can
    /// react like a guest would (e.g. tap "collected" as soon as the robot shows up at the door).</summary>
    public event Func<ServiceNotification, CancellationToken, Task>? NotificationReceived;

    public SimulatedPad(string roomId, IRoomMessageBus bus)
    {
        _roomId = roomId;
        _bus = bus;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _bus.SubscribeAsync(RoomTopics.AllDeviceStatesInRoom(_roomId), OnDeviceStateAsync, ct).ConfigureAwait(false);
        await _bus.SubscribeAsync(RoomTopics.EnvState(_roomId), OnEnvStateAsync, ct).ConfigureAwait(false);
        await _bus.SubscribeAsync(RoomTopics.GatewayStatus(_roomId), OnGatewayStatusAsync, ct).ConfigureAwait(false);
        await _bus.SubscribeAsync(RoomTopics.ServiceNotify(_roomId), OnServiceNotifyAsync, ct).ConfigureAwait(false);
        await _bus.SubscribeAsync(RoomTopics.AllDeviceSetAcksInRoom(_roomId), OnDeviceSetAckAsync, ct).ConfigureAwait(false);
        await _bus.SubscribeAsync(RoomTopics.SceneSetAck(_roomId), OnSceneSetAckAsync, ct).ConfigureAwait(false);
    }

    /// <summary>Watches a hotel-wide facility's occupancy (e.g. the gym tile on the Pad's amenities tab).</summary>
    public async Task WatchFacilityAsync(string facilityId, CancellationToken ct = default) =>
        await _bus.SubscribeAsync(RoomTopics.FacilityOccupancy(facilityId), OnFacilityOccupancyAsync, ct).ConfigureAwait(false);

    public IReadOnlyDictionary<string, DeviceState> DeviceCache => _deviceCache;
    public RoomEnvState? EnvCache => _envCache;
    public bool GatewayOnline => _gatewayOnline;
    public IReadOnlyDictionary<string, FacilityOccupancyState> FacilityCache => _facilityCache;

    public Task SendDeviceCommandAsync(DeviceCommand command, CancellationToken ct = default) =>
        _bus.PublishAsync(RoomTopics.DeviceSet(_roomId, command.DeviceId), JsonSerializer.Serialize(command), retain: false, ct);

    public Task SendSceneAsync(SceneSetCommand scene, CancellationToken ct = default) =>
        _bus.PublishAsync(RoomTopics.SceneSet(_roomId), JsonSerializer.Serialize(scene), retain: false, ct);

    public Task SendServiceRequestAsync(GuestServiceRequest request, CancellationToken ct = default) =>
        _bus.PublishAsync(RoomTopics.ServiceOrder(_roomId), JsonSerializer.Serialize(request), retain: false, ct);

    private Task OnDeviceStateAsync(string topic, string payload, CancellationToken ct)
    {
        var state = JsonSerializer.Deserialize<DeviceState>(payload);
        if (state is not null)
        {
            _deviceCache[state.DeviceId] = state;
        }
        return Task.CompletedTask;
    }

    private Task OnEnvStateAsync(string topic, string payload, CancellationToken ct)
    {
        _envCache = JsonSerializer.Deserialize<RoomEnvState>(payload);
        return Task.CompletedTask;
    }

    private Task OnGatewayStatusAsync(string topic, string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        _gatewayOnline = doc.RootElement.GetProperty("Online").GetBoolean();
        return Task.CompletedTask;
    }

    private async Task OnServiceNotifyAsync(string topic, string payload, CancellationToken ct)
    {
        var notification = JsonSerializer.Deserialize<ServiceNotification>(payload);
        if (notification is null)
        {
            return;
        }

        Console.WriteLine($"  [Pad<-通知] {notification.Category}: {notification.Message}");

        if (NotificationReceived is not null)
        {
            await NotificationReceived(notification, ct).ConfigureAwait(false);
        }
    }

    private Task OnDeviceSetAckAsync(string topic, string payload, CancellationToken ct)
    {
        var ack = JsonSerializer.Deserialize<CommandAck>(payload);
        if (ack is not null)
        {
            var dup = ack.WasDuplicate ? "（重複指令，未重複套用）" : "";
            Console.WriteLine($"  [Pad<-ACK] 裝置指令 {ack.CommandId} success={ack.Success}{dup}");
        }
        return Task.CompletedTask;
    }

    private Task OnSceneSetAckAsync(string topic, string payload, CancellationToken ct)
    {
        var ack = JsonSerializer.Deserialize<CommandAck>(payload);
        if (ack is not null)
        {
            Console.WriteLine($"  [Pad<-ACK] 情境指令 {ack.CommandId} success={ack.Success} — {ack.Message}");
        }
        return Task.CompletedTask;
    }

    private Task OnFacilityOccupancyAsync(string topic, string payload, CancellationToken ct)
    {
        var occupancy = JsonSerializer.Deserialize<FacilityOccupancyState>(payload);
        if (occupancy is not null)
        {
            _facilityCache[occupancy.FacilityId] = occupancy;
            var peakTag = occupancy.IsPeak ? "（尖峰警示）" : "";
            Console.WriteLine($"  [Pad<-設施] {occupancy.DisplayName} 目前 {occupancy.CurrentCount}/{occupancy.Capacity} 人{peakTag}");
        }
        return Task.CompletedTask;
    }
}
