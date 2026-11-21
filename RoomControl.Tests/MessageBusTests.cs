using RoomControl.Sim;

namespace RoomControl.Tests;

public class MessageBusTests
{
    [Fact]
    public async Task Retained_message_is_delivered_to_late_subscribers_matching_wildcards()
    {
        var bus = new InMemoryMessageBus();
        await bus.PublishAsync("room/R1/device/light-1/state", "on", retain: true, ct: TestContext.Current.CancellationToken);
        await bus.PublishAsync("room/R1/device/light-1/set", "cmd", retain: false, ct: TestContext.Current.CancellationToken);
        await bus.PublishAsync("room/R2/device/tv/state", "off", retain: true, ct: TestContext.Current.CancellationToken);

        var plus = await MessageRecorder.AttachAsync(bus, "room/R1/device/+/state");
        var hash = await MessageRecorder.AttachAsync(bus, "room/#");

        Assert.Equal(["on"], plus.Messages.Select(m => m.Payload));
        Assert.Equal(2, hash.Messages.Count); // only the two retained state messages, not the non-retained set
        Assert.DoesNotContain(plus.Messages, m => m.Topic.Contains("R2"));
    }

    [Fact]
    public async Task Last_will_fires_on_ungraceful_disconnect_only()
    {
        var bus = new InMemoryMessageBus();
        var status = await MessageRecorder.AttachAsync(bus, "room/+/gateway/status");

        bus.Connect("gw-1");
        bus.RegisterLastWill("gw-1", "room/R1/gateway/status", "offline");
        bus.DisconnectGracefully("gw-1");
        await bus.DisconnectUngracefullyAsync("gw-1", TestContext.Current.CancellationToken);
        Assert.Empty(status.Messages);

        bus.Connect("gw-2");
        bus.RegisterLastWill("gw-2", "room/R2/gateway/status", "offline");
        await bus.DisconnectUngracefullyAsync("gw-2", TestContext.Current.CancellationToken);
        Assert.Equal(["offline"], status.Messages.Select(m => m.Payload));
    }
}
