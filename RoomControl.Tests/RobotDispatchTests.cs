using System.Collections.Concurrent;
using System.Text.Json;
using RoomControl.Core.Commands;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;
using RoomControl.Sim;
using RoomControl.Sim.Services;

namespace RoomControl.Tests;

public class RobotDispatchTests
{
    private static FoodOrderRequest Order(string id, string room = "R101") => new()
    {
        RequestId = id,
        RoomId = room,
        RequestedAtUtc = DateTimeOffset.UtcNow,
        Items = [new FoodOrderItem { Name = "Congee", Quantity = 1 }]
    };

    private static Task SendAsync(IRoomMessageBus bus, GuestServiceRequest request) =>
        bus.PublishAsync(RoomTopics.ServiceOrder(request.RoomId), JsonSerializer.Serialize(request));

    /// <summary>Records order notifications; optionally confirms pickup the moment the robot arrives.</summary>
    private sealed class OrderLog
    {
        public ConcurrentQueue<ServiceNotification> Notifications { get; } = new();

        public static async Task<OrderLog> AttachAsync(IRoomMessageBus bus, string room, bool confirmOnArrival)
        {
            var log = new OrderLog();
            await bus.SubscribeAsync(RoomTopics.ServiceNotify(room), async (_, payload, ct) =>
            {
                var n = JsonSerializer.Deserialize<ServiceNotification>(payload)!;
                log.Notifications.Enqueue(n);
                if (confirmOnArrival && n.OrderStatus == OrderStatus.ArrivedAtDoor)
                {
                    await SendAsync(bus, new ConfirmPickupRequest { RequestId = Guid.NewGuid().ToString("N"), RoomId = room, RequestedAtUtc = DateTimeOffset.UtcNow, OrderId = n.OrderId! });
                }
            });
            return log;
        }

        public bool HasStatus(string orderId, OrderStatus status) =>
            Notifications.Any(n => n.OrderId == orderId && n.OrderStatus == status);
    }

    private static async Task<(InMemoryMessageBus Bus, MessageRecorder RobotStatus)> StartAsync(string[] robots, TimeSpan pickupTimeout)
    {
        var bus = new InMemoryMessageBus();
        var robotStatus = await MessageRecorder.AttachAsync(bus, RoomTopics.RobotStatusWildcard);
        await new RobotDispatchService(bus, robots, pickupTimeout).StartAsync();
        return (bus, robotStatus);
    }

    private static bool RobotBackToIdle(MessageRecorder status, string robotId) =>
        status.Decode<RobotStatusSnapshot>().LastOrDefault(s => s.RobotId == robotId)?.State == RobotState.Idle;

    [Fact]
    public async Task Confirmed_pickup_delivers_the_order_and_frees_the_robot()
    {
        var (bus, robotStatus) = await StartAsync(["bot-1"], TimeSpan.FromSeconds(5));
        var log = await OrderLog.AttachAsync(bus, "R101", confirmOnArrival: true);

        await SendAsync(bus, Order("order-1"));
        await TestSupport.WaitUntilAsync(() => log.HasStatus("order-1", OrderStatus.Delivered) && RobotBackToIdle(robotStatus, "bot-1"), "delivery and robot idle");

        var statuses = log.Notifications.Where(n => n.OrderStatus is not null).Select(n => n.OrderStatus!.Value).ToArray();
        Assert.Equal(
            [OrderStatus.Placed, OrderStatus.Preparing, OrderStatus.RobotAssigned, OrderStatus.EnRoute, OrderStatus.ArrivedAtDoor, OrderStatus.Delivered],
            statuses);
        Assert.DoesNotContain(robotStatus.Decode<RobotStatusSnapshot>(), s => s.State == RobotState.Returning);
    }

    [Fact]
    public async Task Unclaimed_order_times_out_and_the_robot_returns_to_base_then_idles()
    {
        var (bus, robotStatus) = await StartAsync(["bot-1"], TimeSpan.FromMilliseconds(50));
        var log = await OrderLog.AttachAsync(bus, "R101", confirmOnArrival: false);

        await SendAsync(bus, Order("order-1"));
        await TestSupport.WaitUntilAsync(() => log.HasStatus("order-1", OrderStatus.TimedOutReturning) && RobotBackToIdle(robotStatus, "bot-1"), "timeout and robot idle");

        Assert.False(log.HasStatus("order-1", OrderStatus.Delivered));
        var states = robotStatus.Decode<RobotStatusSnapshot>().Select(s => s.State).ToList();
        Assert.Contains(RobotState.Returning, states);
        Assert.True(states.IndexOf(RobotState.Returning) < states.LastIndexOf(RobotState.Idle));
    }

    [Fact]
    public async Task Concurrent_orders_never_double_book_a_robot_and_queued_orders_are_still_delivered()
    {
        string[] robots = ["bot-1", "bot-2"];
        var (bus, robotStatus) = await StartAsync(robots, TimeSpan.FromSeconds(5));
        var log = await OrderLog.AttachAsync(bus, "R101", confirmOnArrival: true);

        var orderIds = Enumerable.Range(1, 6).Select(i => $"order-{i}").ToArray();
        await Task.WhenAll(orderIds.Select(id => Task.Run(() => SendAsync(bus, Order(id)))));

        await TestSupport.WaitUntilAsync(
            () => orderIds.All(id => log.HasStatus(id, OrderStatus.Delivered)) && robots.All(r => RobotBackToIdle(robotStatus, r)),
            "all orders delivered");

        // Invariant: a robot is only assigned a new order after its previous one reached a terminal status.
        var activeOrderByRobot = new Dictionary<string, string>();
        foreach (var n in log.Notifications.Where(n => n.RobotId is not null))
        {
            switch (n.OrderStatus)
            {
                case OrderStatus.RobotAssigned:
                    Assert.False(activeOrderByRobot.ContainsKey(n.RobotId!), $"{n.RobotId} double-booked: {n.OrderId} assigned while serving {activeOrderByRobot.GetValueOrDefault(n.RobotId!)}");
                    activeOrderByRobot[n.RobotId!] = n.OrderId!;
                    break;
                case OrderStatus.Delivered or OrderStatus.TimedOutReturning:
                    Assert.Equal(n.OrderId, activeOrderByRobot[n.RobotId!]);
                    activeOrderByRobot.Remove(n.RobotId!);
                    break;
            }
        }

        // Each order was assigned exactly once.
        var assignments = log.Notifications.Where(n => n.OrderStatus == OrderStatus.RobotAssigned).Select(n => n.OrderId).ToArray();
        Assert.Equal(orderIds.Order(), assignments.Order());
    }

    [Fact]
    public async Task Redelivered_food_order_creates_one_order_and_reserves_one_robot()
    {
        var (bus, robotStatus) = await StartAsync(["bot-1", "bot-2"], TimeSpan.FromSeconds(5));
        var log = await OrderLog.AttachAsync(bus, "R101", confirmOnArrival: true);

        var order = Order("dup-1");
        await SendAsync(bus, order);
        await SendAsync(bus, order); // QoS1-style redelivery of the identical request
        await SendAsync(bus, order);

        await TestSupport.WaitUntilAsync(() => log.HasStatus("dup-1", OrderStatus.Delivered) && RobotBackToIdle(robotStatus, "bot-1") && RobotBackToIdle(robotStatus, "bot-2"), "delivery and robots idle");

        Assert.Single(log.Notifications, n => n.OrderStatus == OrderStatus.Placed);
        Assert.Single(log.Notifications, n => n.OrderStatus == OrderStatus.RobotAssigned);
        Assert.Single(log.Notifications, n => n.OrderStatus == OrderStatus.Delivered);
    }

    [Fact]
    public async Task Order_whose_notification_failed_is_retried_on_redelivery_and_processed_once()
    {
        var (bus, _) = await StartAsync(["bot-1"], TimeSpan.FromMilliseconds(100));
        var attempts = 0;
        await bus.SubscribeAsync(RoomTopics.ServiceNotify("R101"), (_, _, _) =>
            Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("injected") : Task.CompletedTask, TestContext.Current.CancellationToken);
        var log = await OrderLog.AttachAsync(bus, "R101", confirmOnArrival: true);

        var order = Order("order-1");
        await SendAsync(bus, order); // first attempt fails on its Placed notification (logged in the background)
        // Redeliver until the failed attempt's record is released and the retry is accepted; duplicates
        // arriving earlier (still in flight) or later (already accepted) are ignored.
        await TestSupport.WaitUntilAsync(() =>
        {
            SendAsync(bus, order).GetAwaiter().GetResult();
            return log.HasStatus("order-1", OrderStatus.Placed);
        }, "retry accepted");
        await TestSupport.WaitUntilAsync(() => log.HasStatus("order-1", OrderStatus.Delivered), "order delivered");

        Assert.Single(log.Notifications, n => n.OrderStatus == OrderStatus.Placed);
        Assert.Single(log.Notifications, n => n.OrderStatus == OrderStatus.Delivered);
    }

    [Fact]
    public async Task Duplicate_order_arriving_while_first_is_in_flight_is_ignored()
    {
        var (bus, _) = await StartAsync(["bot-1"], TimeSpan.FromMilliseconds(100));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await bus.SubscribeAsync(RoomTopics.ServiceNotify("R101"), async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        }, TestContext.Current.CancellationToken);
        var log = await OrderLog.AttachAsync(bus, "R101", confirmOnArrival: true);

        var order = Order("order-1");
        await SendAsync(bus, order); // detached workflow blocks on its Placed notification
        await entered.Task.WaitAsync(TestSupport.WaitLimit, TestContext.Current.CancellationToken);
        await SendAsync(bus, order); // duplicate while in flight
        release.SetResult();
        await TestSupport.WaitUntilAsync(() => log.HasStatus("order-1", OrderStatus.Delivered), "order delivered");

        Assert.Single(log.Notifications, n => n.OrderStatus == OrderStatus.Placed);
        Assert.Single(log.Notifications, n => n.OrderStatus == OrderStatus.Delivered);
    }

    [Fact]
    public async Task Duplicate_order_arriving_during_a_failed_acceptance_is_retried_so_the_order_is_not_lost()
    {
        var (bus, _) = await StartAsync(["bot-1"], TimeSpan.FromMilliseconds(100));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        // Registered before the log, so the failing first Placed notification never reaches it.
        await bus.SubscribeAsync(RoomTopics.ServiceNotify("R101"), async (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("injected");
            }
        }, TestContext.Current.CancellationToken);
        var log = await OrderLog.AttachAsync(bus, "R101", confirmOnArrival: true);

        var order = Order("order-1");
        await SendAsync(bus, order); // first acceptance blocks on its Placed notification
        await entered.Task.WaitAsync(TestSupport.WaitLimit, TestContext.Current.CancellationToken);
        await SendAsync(bus, order); // duplicate arrives while acceptance is in flight
        release.SetResult();        // first acceptance fails; the gate retries the duplicate in the background

        await TestSupport.WaitUntilAsync(() => log.HasStatus("order-1", OrderStatus.Delivered), "order delivered");
        Assert.Single(log.Notifications, n => n.OrderStatus == OrderStatus.Placed);
        Assert.Single(log.Notifications, n => n.OrderStatus == OrderStatus.Delivered);
    }
}
