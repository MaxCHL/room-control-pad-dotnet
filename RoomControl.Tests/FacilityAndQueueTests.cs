using System.Text.Json;
using RoomControl.Core.Commands;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;
using RoomControl.Sim;
using RoomControl.Sim.Services;

namespace RoomControl.Tests;

public class FacilityAndQueueTests
{
    [Fact]
    public async Task Occupancy_is_published_retained_and_flags_peak_at_80_percent()
    {
        var bus = new InMemoryMessageBus();
        var service = new FacilityOccupancyService(bus);
        await service.RegisterFacilityAsync("gym", "Gym", capacity: 10, initialCount: 7, ct: TestContext.Current.CancellationToken);
        await service.UpdateOccupancyAsync("gym", 8, TestContext.Current.CancellationToken);

        // A late subscriber receives only the latest retained state.
        var late = await MessageRecorder.AttachAsync(bus, RoomTopics.FacilityOccupancy("gym"));
        var state = Assert.Single(late.Decode<FacilityOccupancyState>());

        Assert.Equal(8, state.CurrentCount);
        Assert.Equal(10, state.Capacity);
        Assert.True(state.IsPeak);

        await service.UpdateOccupancyAsync("gym", 7, TestContext.Current.CancellationToken);
        Assert.False(late.Decode<FacilityOccupancyState>().Last().IsPeak);
    }

    [Fact]
    public async Task Updating_an_unknown_facility_is_ignored_without_throwing_or_publishing()
    {
        var bus = new InMemoryMessageBus();
        var service = new FacilityOccupancyService(bus);
        var all = await MessageRecorder.AttachAsync(bus, "hotel/facility/#");

        await service.UpdateOccupancyAsync("ghost", 5, TestContext.Current.CancellationToken);

        Assert.Empty(all.Messages);
    }

    [Fact]
    public async Task Concurrent_updates_are_all_applied_and_published_without_corrupting_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new InMemoryMessageBus();
        var service = new FacilityOccupancyService(bus);
        await service.RegisterFacilityAsync("pool", "Pool", capacity: 500, initialCount: 0, ct: ct);
        var live = await MessageRecorder.AttachAsync(bus, RoomTopics.FacilityOccupancy("pool"));

        await Task.WhenAll(Enumerable.Range(1, 200)
            .Select(n => Task.Run(() => service.UpdateOccupancyAsync("pool", n, ct), ct)));

        // Every update is published exactly once (plus the initial retained replay). Publish order across
        // concurrent updates is not guaranteed (documented shortcut), but the retained value is always one
        // of the published states.
        var published = live.Decode<FacilityOccupancyState>();
        Assert.Equal(201, published.Count);
        Assert.Equal(Enumerable.Range(1, 200), published.Skip(1).Select(p => p.CurrentCount).Order());
        var late = await MessageRecorder.AttachAsync(bus, RoomTopics.FacilityOccupancy("pool"));
        var retained = Assert.Single(late.Decode<FacilityOccupancyState>());
        Assert.Contains(published, p => p.CurrentCount == retained.CurrentCount);
    }

    [Fact]
    public async Task Subscriber_can_update_occupancy_inline_or_via_TaskRun_without_deadlocking()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new InMemoryMessageBus();
        var service = new FacilityOccupancyService(bus);
        await service.RegisterFacilityAsync("gym", "Gym", capacity: 10, initialCount: 1, ct: ct);
        Task? scheduled = null;
        await bus.SubscribeAsync(RoomTopics.FacilityOccupancy("gym"), (_, payload, token) =>
        {
            var count = JsonSerializer.Deserialize<FacilityOccupancyState>(payload)!.CurrentCount;
            if (count == 5)
            {
                return service.UpdateOccupancyAsync("gym", 6, token); // inline re-entry from the handler
            }

            if (count == 6)
            {
                scheduled = Task.Run(() => service.UpdateOccupancyAsync("gym", 7, ct), ct); // scheduled from the handler
            }

            return Task.CompletedTask;
        }, ct);

        await service.UpdateOccupancyAsync("gym", 5, ct).WaitAsync(TestSupport.WaitLimit, ct);
        await scheduled!.WaitAsync(TestSupport.WaitLimit, ct);

        var late = await MessageRecorder.AttachAsync(bus, RoomTopics.FacilityOccupancy("gym"));
        Assert.Equal(7, Assert.Single(late.Decode<FacilityOccupancyState>()).CurrentCount);
    }

    [Fact]
    public async Task Queue_calls_tickets_in_order_notifies_the_room_and_tracks_waiting_count()
    {
        var bus = new InMemoryMessageBus();
        var queue = new RestaurantQueueService(bus, "bistro", "Bistro", TimeSpan.FromMinutes(5));
        await queue.StartAsync(TestContext.Current.CancellationToken);
        var queueStates = await MessageRecorder.AttachAsync(bus, RoomTopics.RestaurantQueue("bistro"));
        var notifications = await MessageRecorder.AttachAsync(bus, RoomTopics.ServiceNotify("R102"));

        var first = await queue.TakeTicketAsync("R101", 2, TestContext.Current.CancellationToken);
        var second = await queue.TakeTicketAsync("R102", 4, TestContext.Current.CancellationToken);
        Assert.Equal("BISTRO-001", first.TicketNumber);
        Assert.Equal("BISTRO-002", second.TicketNumber);

        var afterTaking = queueStates.Decode<RestaurantQueueState>().Last();
        Assert.Equal(2, afterTaking.WaitingCount);
        Assert.Equal(10, afterTaking.EstimatedWaitMinutes);

        var called = await queue.CallNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first.TicketNumber, called!.TicketNumber);
        Assert.Equal(QueueStatus.Called, called.Status);
        Assert.Empty(notifications.Messages); // the first ticket belongs to R101, not R102

        var afterCall = queueStates.Decode<RestaurantQueueState>().Last();
        Assert.Equal(1, afterCall.WaitingCount);
        Assert.Equal(first.TicketNumber, afterCall.NowCallingTicket);

        var calledSecond = await queue.CallNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(second.TicketNumber, calledSecond!.TicketNumber);
        var notice = Assert.Single(notifications.Decode<ServiceNotification>());
        Assert.Contains(second.TicketNumber, notice.Message);

        Assert.Null(await queue.CallNextAsync(TestContext.Current.CancellationToken));
    }
}
