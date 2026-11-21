using System.Text.Json;
using RoomControl.Core.Commands;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;

namespace RoomControl.Sim.Services;

/// <summary>
/// Backend service for restaurant walk-in queues: a guest takes a ticket from the Pad,
/// the restaurant's aggregate queue state is published retained (so any Pad can show the
/// current wait), and calling the next ticket also pushes a direct notification back to the
/// originating room's Pad.
/// </summary>
public sealed class RestaurantQueueService
{
    private readonly IRoomMessageBus _bus;
    private readonly string _restaurantId;
    private readonly string _displayName;
    private readonly TimeSpan _averageWaitPerParty;
    private readonly List<RestaurantQueueTicket> _tickets = new();
    private int _ticketSequence;

    public RestaurantQueueService(IRoomMessageBus bus, string restaurantId, string displayName, TimeSpan? averageWaitPerParty = null)
    {
        _bus = bus;
        _restaurantId = restaurantId;
        _displayName = displayName;
        _averageWaitPerParty = averageWaitPerParty ?? TimeSpan.FromMinutes(5);
    }

    public Task StartAsync(CancellationToken ct = default) => PublishQueueStateAsync(nowCalling: null, ct);

    public async Task<RestaurantQueueTicket> TakeTicketAsync(string roomId, int partySize, CancellationToken ct = default)
    {
        _ticketSequence++;
        var ticket = new RestaurantQueueTicket
        {
            TicketNumber = $"{_restaurantId.ToUpperInvariant()}-{_ticketSequence:D3}",
            RestaurantId = _restaurantId,
            RoomId = roomId,
            PartySize = partySize,
            Status = QueueStatus.Waiting,
            IssuedAtUtc = DateTimeOffset.UtcNow
        };
        _tickets.Add(ticket);
        await PublishQueueStateAsync(nowCalling: null, ct).ConfigureAwait(false);
        return ticket;
    }

    /// <summary>Calls the next waiting ticket and pushes a notification to that ticket's room.</summary>
    public async Task<RestaurantQueueTicket?> CallNextAsync(CancellationToken ct = default)
    {
        var index = _tickets.FindIndex(t => t.Status == QueueStatus.Waiting);
        if (index < 0)
        {
            return null;
        }

        var called = _tickets[index] with { Status = QueueStatus.Called };
        _tickets[index] = called;

        await PublishQueueStateAsync(called.TicketNumber, ct).ConfigureAwait(false);

        var notification = new ServiceNotification
        {
            RoomId = called.RoomId,
            Category = "Restaurant",
            Message = $"您的候位號碼 {called.TicketNumber} 已叫號，請前往 {_displayName} 用餐。",
            AtUtc = DateTimeOffset.UtcNow
        };
        await _bus.PublishAsync(RoomTopics.ServiceNotify(called.RoomId), JsonSerializer.Serialize(notification), retain: false, ct).ConfigureAwait(false);

        return called;
    }

    private Task PublishQueueStateAsync(string? nowCalling, CancellationToken ct)
    {
        var waitingCount = _tickets.Count(t => t.Status == QueueStatus.Waiting);
        var state = new RestaurantQueueState
        {
            RestaurantId = _restaurantId,
            DisplayName = _displayName,
            WaitingCount = waitingCount,
            NowCallingTicket = nowCalling,
            EstimatedWaitMinutes = (int)(_averageWaitPerParty.TotalMinutes * waitingCount),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        return _bus.PublishAsync(RoomTopics.RestaurantQueue(_restaurantId), JsonSerializer.Serialize(state), retain: true, ct);
    }
}
