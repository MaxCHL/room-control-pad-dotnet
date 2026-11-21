using System.Text.Json;
using RoomControl.Core.Commands;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;
using RoomControl.Sim;
using RoomControl.Sim.Services;

namespace RoomControl.Tests;

public class GuestServicesTests
{
    private static HousekeepingRequest Request(string id, GuestRequestKind kind) => new()
    {
        RequestId = id,
        RoomId = "R101",
        RequestedAtUtc = DateTimeOffset.UtcNow,
        Kind = kind
    };

    private static Task SendAsync(IRoomMessageBus bus, GuestServiceRequest request) =>
        bus.PublishAsync(RoomTopics.ServiceOrder(request.RoomId), JsonSerializer.Serialize(request), ct: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Redelivered_guest_request_is_handled_once_and_notified_once()
    {
        var bus = new InMemoryMessageBus();
        await new GuestServicesService(bus).StartAsync(TestContext.Current.CancellationToken);
        var notifications = await MessageRecorder.AttachAsync(bus, RoomTopics.ServiceNotify("R101"));

        var cleaning = Request("req-1", GuestRequestKind.Cleaning);
        await SendAsync(bus, cleaning);
        await SendAsync(bus, cleaning); // QoS1-style redelivery
        await SendAsync(bus, Request("req-2", GuestRequestKind.BillInquiry)); // a distinct request is still handled

        var categories = notifications.Decode<ServiceNotification>().Select(n => n.Category).ToArray();
        Assert.Equal(["Housekeeping", "Billing"], categories);
    }

    [Fact]
    public async Task Request_whose_publish_failed_is_retried_on_redelivery_and_handled_once()
    {
        var bus = new InMemoryMessageBus();
        await new GuestServicesService(bus).StartAsync(TestContext.Current.CancellationToken);
        var attempts = 0;
        await bus.SubscribeAsync(RoomTopics.ServiceNotify("R101"), (_, _, _) =>
            Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("injected") : Task.CompletedTask, TestContext.Current.CancellationToken);
        var notifications = await MessageRecorder.AttachAsync(bus, RoomTopics.ServiceNotify("R101"));

        var cleaning = Request("req-1", GuestRequestKind.Cleaning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(bus, cleaning)); // first attempt fails
        await SendAsync(bus, cleaning); // redelivery retries
        await SendAsync(bus, cleaning); // further redelivery is a duplicate

        Assert.Single(notifications.Decode<ServiceNotification>());
    }

    [Fact]
    public async Task Duplicate_arriving_while_first_is_in_flight_returns_immediately_and_is_not_processed_twice()
    {
        var bus = new InMemoryMessageBus();
        await new GuestServicesService(bus).StartAsync(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await bus.SubscribeAsync(RoomTopics.ServiceNotify("R101"), async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        }, TestContext.Current.CancellationToken);
        var notifications = await MessageRecorder.AttachAsync(bus, RoomTopics.ServiceNotify("R101"));

        var cleaning = Request("req-1", GuestRequestKind.Cleaning);
        var first = SendAsync(bus, cleaning); // blocks inside publish
        await entered.Task.WaitAsync(TestSupport.WaitLimit, TestContext.Current.CancellationToken);
        await SendAsync(bus, cleaning).WaitAsync(TestSupport.WaitLimit, TestContext.Current.CancellationToken); // never waits on the first
        release.SetResult();
        await first;

        Assert.Single(notifications.Decode<ServiceNotification>());
    }

    [Fact]
    public async Task Duplicate_arriving_during_an_attempt_that_fails_is_retried_so_the_request_is_not_lost()
    {
        var bus = new InMemoryMessageBus();
        await new GuestServicesService(bus).StartAsync(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        // Registered before the recorder, so a failing first delivery never reaches the recorder.
        await bus.SubscribeAsync(RoomTopics.ServiceNotify("R101"), async (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("injected");
            }
        }, TestContext.Current.CancellationToken);
        var notifications = await MessageRecorder.AttachAsync(bus, RoomTopics.ServiceNotify("R101"));

        var cleaning = Request("req-1", GuestRequestKind.Cleaning);
        var first = SendAsync(bus, cleaning);
        await entered.Task.WaitAsync(TestSupport.WaitLimit, TestContext.Current.CancellationToken);
        await SendAsync(bus, cleaning); // duplicate while in flight: returns at once, retry recorded
        release.SetResult();           // the first attempt now fails

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await TestSupport.WaitUntilAsync(() => notifications.Messages.Count == 1, "background retry delivered");
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Redelivery_scheduled_from_a_handler_after_the_attempt_failed_is_processed()
    {
        var bus = new InMemoryMessageBus();
        await new GuestServicesService(bus).StartAsync(TestContext.Current.CancellationToken);
        var attempts = 0;
        Task? redelivery = null;
        var cleaning = Request("req-1", GuestRequestKind.Cleaning);
        await bus.SubscribeAsync(RoomTopics.ServiceNotify("R101"), (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                // A flow forked inside the attempt redelivers later, after the attempt has failed.
                redelivery = Task.Run(async () => { await Task.Delay(100); await SendAsync(bus, cleaning); });
                throw new InvalidOperationException("injected");
            }
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        var notifications = await MessageRecorder.AttachAsync(bus, RoomTopics.ServiceNotify("R101"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(bus, cleaning));
        await redelivery!.WaitAsync(TestSupport.WaitLimit, TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
        Assert.Single(notifications.Decode<ServiceNotification>());
    }

    [Fact]
    public async Task Redelivery_from_inside_the_same_attempt_does_not_deadlock()
    {
        var bus = new InMemoryMessageBus();
        await new GuestServicesService(bus).StartAsync(TestContext.Current.CancellationToken);
        var cleaning = Request("req-1", GuestRequestKind.Cleaning);
        // A subscriber that synchronously re-publishes the same request while the first attempt is still running.
        await bus.SubscribeAsync(RoomTopics.ServiceNotify("R101"), (_, _, _) => SendAsync(bus, cleaning), TestContext.Current.CancellationToken);
        var notifications = await MessageRecorder.AttachAsync(bus, RoomTopics.ServiceNotify("R101"));

        await SendAsync(bus, cleaning).WaitAsync(TestSupport.WaitLimit, TestContext.Current.CancellationToken);

        Assert.Single(notifications.Decode<ServiceNotification>());
    }

    [Fact]
    public async Task Persistently_failing_request_that_redelivers_itself_inline_is_retried_at_most_once()
    {
        var bus = new InMemoryMessageBus();
        await new GuestServicesService(bus).StartAsync(TestContext.Current.CancellationToken);
        var cleaning = Request("req-1", GuestRequestKind.Cleaning);
        var attempts = 0;
        await bus.SubscribeAsync(RoomTopics.ServiceNotify("R101"), async (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            await SendAsync(bus, cleaning);                   // inline redelivery while in flight
            throw new InvalidOperationException("pad down"); // and the handling keeps failing
        }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(bus, cleaning));
        await TestSupport.WaitUntilAsync(() => Volatile.Read(ref attempts) >= 2, "background retry ran");
        await Task.Delay(500, TestContext.Current.CancellationToken); // a retry loop would keep growing here

        Assert.Equal(2, Volatile.Read(ref attempts)); // original + exactly one background retry
    }
}
