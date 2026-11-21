using System.Collections.Concurrent;
using System.Text.Json;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;

namespace RoomControl.Sim.Services;

/// <summary>
/// Backend service that owns hotel-wide facility occupancy (gym, pool, business center...).
/// Publishes retained counts so any Pad's amenities tab shows the current status immediately
/// on load, and re-publishes on every update so watching Pads get a live push.
/// </summary>
public sealed class FacilityOccupancyService
{
    private readonly IRoomMessageBus _bus;
    // Thread-safe map; each update is an atomic read-modify-write. No lock is held while publishing,
    // so a subscriber may call back into this service (inline or via Task.Run) without deadlocking.
    // shortcut: concurrent updates of the SAME facility may be published out of order, so the retained
    // value can briefly be an older count (like independent MQTT publishers); production would carry
    // a version/timestamp in the payload or funnel updates through a single writer.
    private readonly ConcurrentDictionary<string, FacilityOccupancyState> _facilities = new();

    public FacilityOccupancyService(IRoomMessageBus bus)
    {
        _bus = bus;
    }

    public Task RegisterFacilityAsync(string facilityId, string displayName, int capacity, int initialCount, CancellationToken ct = default)
    {
        var state = new FacilityOccupancyState
        {
            FacilityId = facilityId,
            DisplayName = displayName,
            CurrentCount = initialCount,
            Capacity = capacity,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _facilities[facilityId] = state;
        return PublishAsync(state, ct);
    }

    public Task UpdateOccupancyAsync(string facilityId, int newCount, CancellationToken ct = default)
    {
        FacilityOccupancyState? updated = null;
        while (_facilities.TryGetValue(facilityId, out var current))
        {
            var candidate = current with { CurrentCount = newCount, UpdatedAtUtc = DateTimeOffset.UtcNow };
            if (_facilities.TryUpdate(facilityId, candidate, current))
            {
                updated = candidate;
                break;
            }
        }

        if (updated is null)
        {
            // Unknown facility: nothing to update or publish. Log and ignore rather than throw.
            Console.WriteLine($"  [WARN] FacilityOccupancyService 忽略未註冊設施 '{facilityId}' 的人數更新。");
            return Task.CompletedTask;
        }

        return PublishAsync(updated, ct);
    }

    private Task PublishAsync(FacilityOccupancyState state, CancellationToken ct) =>
        _bus.PublishAsync(RoomTopics.FacilityOccupancy(state.FacilityId), JsonSerializer.Serialize(state), retain: true, ct);
}
