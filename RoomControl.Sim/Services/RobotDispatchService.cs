using System.Collections.Concurrent;
using System.Text.Json;
using RoomControl.Core.Commands;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;

namespace RoomControl.Sim.Services;

/// <summary>
/// Backend service that owns the full in-room dining order lifecycle: receives orders and
/// pickup confirmations from any room over the shared service/order channel, drives a small
/// pool of delivery robots, and streams status back to the ordering room's Pad via
/// service/notify.
///
/// Each incoming food order is processed on its own detached background workflow (see
/// <see cref="OnServiceOrderAsync"/>) rather than blocking the publisher, which is what lets
/// several orders be in flight concurrently and genuinely compete for a limited robot pool —
/// the same way a real backend would process requests independently of the MQTT client's
/// publish call. Robot reservation and hand-off are the only steps that touch shared mutable
/// state without a settled, well-defined order, so they are the only steps serialized behind
/// <see cref="_reservationLock"/>; delivery steps that only affect one already-reserved robot
/// (walking, waiting for pickup, etc.) run lock-free and concurrently with other robots.
/// </summary>
public sealed class RobotDispatchService
{
    private readonly IRoomMessageBus _bus;
    private readonly TimeSpan _pickupTimeout;
    private readonly ConcurrentDictionary<string, RobotStatusSnapshot> _robots = new();
    private readonly ConcurrentDictionary<string, FoodOrder> _orders = new();
    private readonly ConcurrentQueue<string> _pendingOrderIds = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pickupSignals = new();

    // At-most-once acceptance per food-order RequestId for QoS1 redeliveries (see IdempotencyGate).
    // "Accepted" = handed to the robot pipeline (robot reserved or order queued). A duplicate that
    // arrives while acceptance is in flight returns immediately; if acceptance then fails, the
    // duplicate is retried once in the background. Once accepted, later notification/delivery failures
    // are NOT retried by a redelivery: the pipeline owns the order and a retry would double-book a
    // robot. In-memory only and unbounded.
    private readonly IdempotencyGate<(string RoomId, string RequestId)> _orderAcceptance = new((key, ex) =>
        Console.WriteLine($"  [ERROR] RobotDispatchService 背景重試訂單 {key.RequestId} 失敗（受理或之後的送餐流程）：{ex.GetBaseException().Message}"));

    // Serializes "find an idle robot and reserve it" / "robot freed up, hand off to next
    // queued order or go Idle" so the two decisions can never race each other or double-book
    // a robot. Held only across in-memory dictionary/queue operations, never across an await
    // that represents simulated travel time or waiting for the guest.
    private readonly SemaphoreSlim _reservationLock = new(1, 1);

    public RobotDispatchService(IRoomMessageBus bus, IEnumerable<string> robotIds, TimeSpan? pickupTimeout = null)
    {
        _bus = bus;
        _pickupTimeout = pickupTimeout ?? TimeSpan.FromMilliseconds(600);

        var now = DateTimeOffset.UtcNow;
        foreach (var robotId in robotIds)
        {
            _robots[robotId] = new RobotStatusSnapshot { RobotId = robotId, State = RobotState.Idle, UpdatedAtUtc = now };
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var robot in _robots.Values)
        {
            await PublishRobotStatusAsync(robot, ct).ConfigureAwait(false);
        }

        await _bus.SubscribeAsync(RoomTopics.ServiceOrderWildcard, OnServiceOrderAsync, ct).ConfigureAwait(false);
    }

    private Task OnServiceOrderAsync(string topic, string payload, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<GuestServiceRequest>(payload);
        if (request is null || request.RoomId != RoomTopics.RoomIdOf(topic))
        {
            // A room may only act for itself: the payload's RoomId must match the topic's room.
            Console.WriteLine($"  [WARN] RobotDispatchService 忽略房號與主題不符的請求（topic={topic}）。");
            return Task.CompletedTask;
        }

        switch (request)
        {
            case FoodOrderRequest food:
                // Detached: a food order's full lifecycle can take much longer than a single
                // MQTT publish should block for, so it runs independently of the publisher.
                FireAndForget(HandleFoodOrderAsync(food, ct));
                break;
            case ConfirmPickupRequest confirm:
                HandleConfirmPickup(confirm);
                break;
                // HousekeepingRequest and unrecognized kinds are not this service's concern.
        }
        return Task.CompletedTask;
    }

    private static void FireAndForget(Task task) => task.ContinueWith(
        t => Console.WriteLine($"  [ERROR] RobotDispatchService 背景工作發生未預期例外：{t.Exception!.GetBaseException().Message}"),
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    private async Task HandleFoodOrderAsync(FoodOrderRequest request, CancellationToken ct)
    {
        if (_orders.TryGetValue(request.RequestId, out var other) && other.RoomId != request.RoomId)
        {
            // shortcut: order ids are the client's RequestId; a clash with another room's order is rejected here
            // (production would issue server-side unguessable order ids).
            Console.WriteLine($"  [WARN] RobotDispatchService 忽略與其他房間訂單編號衝突的訂單 {request.RequestId}。");
            return;
        }

        FoodOrder? order = null;
        string? robotId = null;
        var accepted = await _orderAcceptance.RunOnceAsync((request.RoomId, request.RequestId),
            work: async () => { (order, robotId) = await AcceptOrderAsync(request, ct).ConfigureAwait(false); },
            retry: () => HandleFoodOrderAsync(request, CancellationToken.None)).ConfigureAwait(false);

        if (!accepted)
        {
            return; // redelivered order: already accepted, or in flight (retried by the gate if that attempt fails)
        }

        if (robotId is null)
        {
            await NotifyAsync(order!.RoomId, order.OrderId, null, OrderStatus.Preparing, $"訂單 {order.OrderId}：所有送餐機器人忙碌中，已排入等候佇列。", ct).ConfigureAwait(false);
            return;
        }

        await RunRobotDeliveryAsync(robotId, order!.OrderId, ct).ConfigureAwait(false);
    }

    /// <summary>Notifies Placed/Preparing, then reserves a robot or enqueues the order, and marks the request
    /// accepted at that hand-off point (when this returns). Returns the reserved robot id, or null when queued.</summary>
    private async Task<(FoodOrder Order, string? RobotId)> AcceptOrderAsync(FoodOrderRequest request, CancellationToken ct)
    {
        var order = new FoodOrder
        {
            OrderId = request.RequestId,
            RoomId = request.RoomId,
            Items = request.Items,
            Status = OrderStatus.Placed,
            PlacedAtUtc = request.RequestedAtUtc
        };
        _orders[order.OrderId] = order;
        await NotifyAsync(order.RoomId, order.OrderId, null, OrderStatus.Placed, $"訂單 {order.OrderId} 已送出，餐廳準備中。", ct).ConfigureAwait(false);

        await Task.Delay(150, ct).ConfigureAwait(false);
        UpdateOrderStatus(order.OrderId, OrderStatus.Preparing);
        await NotifyAsync(order.RoomId, order.OrderId, null, OrderStatus.Preparing, $"訂單 {order.OrderId} 廚房準備中。", ct).ConfigureAwait(false);

        var robotId = await ReserveRobotOrEnqueueAsync(order.OrderId, order.RoomId, ct).ConfigureAwait(false);
        return (order, robotId);
    }

    /// <summary>Under the reservation lock, finds an Idle robot and reserves it for <paramref name="orderId"/>,
    /// or enqueues the order if every robot is currently busy.</summary>
    private async Task<string?> ReserveRobotOrEnqueueAsync(string orderId, string roomId, CancellationToken ct)
    {
        await _reservationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var idle = _robots.Values.FirstOrDefault(r => r.State == RobotState.Idle);
            if (idle is null)
            {
                _pendingOrderIds.Enqueue(orderId);
                return null;
            }

            _robots[idle.RobotId] = idle with { State = RobotState.Busy, CurrentOrderId = orderId, CurrentRoomId = roomId, UpdatedAtUtc = DateTimeOffset.UtcNow };
            return idle.RobotId;
        }
        finally
        {
            _reservationLock.Release();
        }
    }

    private async Task RunRobotDeliveryAsync(string robotId, string orderId, CancellationToken ct)
    {
        var order = _orders[orderId];

        UpdateOrderStatus(orderId, OrderStatus.RobotAssigned, robotId);
        await SetRobotStateAsync(robotId, RobotState.Busy, orderId, order.RoomId, ct).ConfigureAwait(false);
        await NotifyAsync(order.RoomId, orderId, robotId, OrderStatus.RobotAssigned, $"送餐機器人 {robotId} 已指派訂單 {orderId}。", ct).ConfigureAwait(false);

        await Task.Delay(150, ct).ConfigureAwait(false);
        UpdateOrderStatus(orderId, OrderStatus.EnRoute, robotId);
        await SetRobotStateAsync(robotId, RobotState.EnRoute, orderId, order.RoomId, ct).ConfigureAwait(false);
        await NotifyAsync(order.RoomId, orderId, robotId, OrderStatus.EnRoute, $"送餐機器人 {robotId} 出發前往房間（訂單 {orderId}）。", ct).ConfigureAwait(false);

        await Task.Delay(150, ct).ConfigureAwait(false);
        UpdateOrderStatus(orderId, OrderStatus.ArrivedAtDoor, robotId);
        await SetRobotStateAsync(robotId, RobotState.WaitingForPickup, orderId, order.RoomId, ct).ConfigureAwait(false);

        // The pickup signal MUST be registered before the "arrived at door" notification is
        // published: the Pad's reaction to that very notification (guest tapping "collected")
        // runs synchronously within the NotifyAsync call below, so if the signal were created
        // after it, a confirmation sent immediately upon arrival would be lost.
        var pickupSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pickupSignals[orderId] = pickupSignal;

        await NotifyAsync(order.RoomId, orderId, robotId, OrderStatus.ArrivedAtDoor, $"送餐機器人 {robotId} 已抵達房門，請開門取餐（訂單 {orderId}）。", ct).ConfigureAwait(false);

        var winner = await Task.WhenAny(pickupSignal.Task, Task.Delay(_pickupTimeout, ct)).ConfigureAwait(false);
        _pickupSignals.TryRemove(orderId, out _);

        if (winner == pickupSignal.Task)
        {
            UpdateOrderStatus(orderId, OrderStatus.Delivered, robotId);
            await NotifyAsync(order.RoomId, orderId, robotId, OrderStatus.Delivered, "餐點已取餐，感謝使用送餐機器人服務。", ct).ConfigureAwait(false);
        }
        else
        {
            UpdateOrderStatus(orderId, OrderStatus.TimedOutReturning, robotId);
            await SetRobotStateAsync(robotId, RobotState.Returning, orderId, order.RoomId, ct).ConfigureAwait(false);
            Console.WriteLine($"  [告警->櫃檯值班] 房客逾時未取餐：訂單 {orderId}，機器人 {robotId} 已折返廚房，請協助補送。");
            await NotifyAsync(order.RoomId, orderId, robotId, OrderStatus.TimedOutReturning, "逾時未取餐，機器人已折返廚房，櫃檯將協助補送。", ct).ConfigureAwait(false);
        }

        await Task.Delay(100, ct).ConfigureAwait(false);
        await HandOffOrGoIdleAsync(robotId, ct).ConfigureAwait(false);
    }

    /// <summary>Under the reservation lock, decides, for a robot that just finished a delivery, whether to hand
    /// it straight to the next queued order or mark it Idle — combined into one lock-held
    /// decision so a concurrent <see cref="ReserveRobotOrEnqueueAsync"/> call can never observe
    /// (and double-book) a robot that is "about to" take a queued order.</summary>
    private async Task HandOffOrGoIdleAsync(string robotId, CancellationToken ct)
    {
        string? handoffOrderId = null;

        await _reservationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pendingOrderIds.TryDequeue(out var nextOrderId))
            {
                handoffOrderId = nextOrderId;
            }
            else
            {
                var current = _robots[robotId];
                _robots[robotId] = current with { State = RobotState.Idle, CurrentOrderId = null, CurrentRoomId = null, UpdatedAtUtc = DateTimeOffset.UtcNow };
            }
        }
        finally
        {
            _reservationLock.Release();
        }

        if (handoffOrderId is not null)
        {
            await RunRobotDeliveryAsync(robotId, handoffOrderId, ct).ConfigureAwait(false);
        }
        else
        {
            await PublishRobotStatusAsync(_robots[robotId], ct).ConfigureAwait(false);
        }
    }

    private void HandleConfirmPickup(ConfirmPickupRequest request)
    {
        // Only the room that placed the order may confirm its pickup.
        if (!_orders.TryGetValue(request.OrderId, out var order) || order.RoomId != request.RoomId)
        {
            Console.WriteLine($"  [WARN] RobotDispatchService 拒絕非下單房間的取餐確認（訂單 {request.OrderId}，房間 {request.RoomId}）。");
            return;
        }

        if (_pickupSignals.TryGetValue(request.OrderId, out var signal))
        {
            signal.TrySetResult();
        }
    }

    private void UpdateOrderStatus(string orderId, OrderStatus status, string? robotId = null)
    {
        var order = _orders[orderId];
        _orders[orderId] = order with
        {
            Status = status,
            AssignedRobotId = robotId ?? order.AssignedRobotId,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task SetRobotStateAsync(string robotId, RobotState state, string? orderId, string? roomId, CancellationToken ct)
    {
        var snapshot = new RobotStatusSnapshot
        {
            RobotId = robotId,
            State = state,
            CurrentOrderId = orderId,
            CurrentRoomId = roomId,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _robots[robotId] = snapshot;
        await PublishRobotStatusAsync(snapshot, ct).ConfigureAwait(false);
    }

    private Task PublishRobotStatusAsync(RobotStatusSnapshot snapshot, CancellationToken ct) =>
        _bus.PublishAsync(RoomTopics.RobotStatus(snapshot.RobotId), JsonSerializer.Serialize(snapshot), retain: true, ct);

    private Task NotifyAsync(string roomId, string orderId, string? robotId, OrderStatus status, string message, CancellationToken ct)
    {
        var notification = new ServiceNotification
        {
            RoomId = roomId,
            Category = "Order",
            Message = message,
            OrderId = orderId,
            RobotId = robotId,
            OrderStatus = status,
            AtUtc = DateTimeOffset.UtcNow
        };
        return _bus.PublishAsync(RoomTopics.ServiceNotify(roomId), JsonSerializer.Serialize(notification), retain: false, ct);
    }
}
