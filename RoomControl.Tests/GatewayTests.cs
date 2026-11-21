using System.Collections.Concurrent;
using System.Text.Json;
using RoomControl.Core;
using RoomControl.Core.Commands;
using RoomControl.Core.Devices;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;
using RoomControl.Sim;

namespace RoomControl.Tests;

public class GatewayTests
{
    private const string Room = "R101";

    /// <summary>Wraps the real driver, records apply order, and can fail on a chosen call.</summary>
    private sealed class RecordingDriver(int? failOnCallNumber = null) : IDeviceDriver
    {
        private readonly SimulatedDeviceDriver _inner = new();
        private int _calls;
        public ConcurrentQueue<string> AppliedCommandIds { get; } = new();

        public DeviceState Apply(DeviceState current, DeviceCommand command)
        {
            if (Interlocked.Increment(ref _calls) == failOnCallNumber)
            {
                throw new InvalidOperationException("Injected driver failure");
            }

            AppliedCommandIds.Enqueue(command.CommandId);
            Thread.Sleep(2); // widen the window so a missing lock would let other commands slip in
            return _inner.Apply(current, command);
        }
    }

    private static async Task<(InMemoryMessageBus Bus, SimulatedRoomGateway Gateway)> StartGatewayAsync(IDeviceDriver driver)
    {
        var bus = new InMemoryMessageBus();
        var gateway = new SimulatedRoomGateway(TestSupport.Inventory(Room), bus, driver);
        await gateway.StartAsync();
        return (bus, gateway);
    }

    private static SceneSetCommand Scene(SceneId scene, string? commandId = null) => new()
    {
        CommandId = commandId ?? Guid.NewGuid().ToString("N"),
        RoomId = Room,
        Scene = scene,
        IssuedAtUtc = DateTimeOffset.UtcNow
    };

    private static Task SendAsync(IRoomMessageBus bus, SceneSetCommand scene) =>
        bus.PublishAsync(RoomTopics.SceneSet(Room), JsonSerializer.Serialize(scene));

    [Fact]
    public async Task Scene_is_applied_to_devices_and_acked_with_the_same_command_id()
    {
        var (bus, gateway) = await StartGatewayAsync(new SimulatedDeviceDriver());
        var acks = await MessageRecorder.AttachAsync(bus, RoomTopics.SceneSetAck(Room));

        var command = Scene(SceneId.Away);
        await SendAsync(bus, command);

        var ack = Assert.Single(acks.Decode<CommandAck>());
        Assert.Equal(command.CommandId, ack.CommandId);
        Assert.True(ack.Success);
        Assert.False(ack.WasDuplicate);

        var states = gateway.CurrentDeviceStates;
        Assert.Equal(DoorSignMode.DoNotDisturb, ((DoorSignState)states["doorsign-1"]).Mode);
        Assert.Equal(28.0, ((AirconState)states["aircon-1"]).TargetTemperatureC);
        Assert.Equal(CurtainPosition.Closed, ((CurtainState)states["curtain-1"]).Position);
        Assert.False(((TvState)states["tv-1"]).IsOn);
    }

    [Fact]
    public async Task Redelivered_scene_command_is_acked_as_duplicate_and_not_reapplied()
    {
        var driver = new RecordingDriver();
        var (bus, _) = await StartGatewayAsync(driver);
        var acks = await MessageRecorder.AttachAsync(bus, RoomTopics.SceneSetAck(Room));

        var command = Scene(SceneId.DoNotDisturb);
        await SendAsync(bus, command);
        await SendAsync(bus, command);

        var decoded = acks.Decode<CommandAck>();
        Assert.Equal(2, decoded.Count);
        Assert.False(decoded[0].WasDuplicate);
        Assert.True(decoded[1].WasDuplicate);
        Assert.Equal(command.CommandId, decoded[1].CommandId);
        Assert.Single(driver.AppliedCommandIds); // DoNotDisturb is one device command, applied once
    }

    [Fact]
    public async Task Single_device_commands_never_interleave_with_a_scene()
    {
        var driver = new RecordingDriver();
        var (bus, _) = await StartGatewayAsync(driver);

        var sceneCommands = SceneEngine.BuildSceneCommands(SceneId.Sleep, TestSupport.Inventory(Room), DateTimeOffset.UtcNow);
        var sceneCommandCount = sceneCommands.Count;
        var scene = Scene(SceneId.Sleep);

        var singles = Enumerable.Range(0, 20).Select(i => new TvSetCommand
        {
            CommandId = $"single-{i}",
            RoomId = Room,
            DeviceId = "tv-1",
            IssuedAtUtc = DateTimeOffset.UtcNow,
            Volume = i
        }).ToArray();

        var tasks = singles
            .Select(s => Task.Run(() => bus.PublishAsync(RoomTopics.DeviceSet(Room, s.DeviceId), JsonSerializer.Serialize<DeviceCommand>(s))))
            .Append(Task.Run(() => SendAsync(bus, scene), TestContext.Current.CancellationToken))
            .ToArray();
        await Task.WhenAll(tasks);

        var order = driver.AppliedCommandIds.ToArray();
        Assert.Equal(sceneCommandCount + singles.Length, order.Length);

        // Scene device commands are generated with fresh ids, so every id that is not a
        // "single-N" id belongs to the scene; they must form one contiguous block.
        var sceneIndexes = order.Select((id, i) => (id, i)).Where(x => !x.id.StartsWith("single-")).Select(x => x.i).ToArray();
        Assert.Equal(sceneCommandCount, sceneIndexes.Length);
        Assert.Equal(sceneIndexes[0] + sceneCommandCount - 1, sceneIndexes[^1]);
    }

    [Fact]
    public async Task Driver_failure_mid_scene_publishes_no_success_ack_and_releases_the_gateway_lock()
    {
        // The code does NOT roll back: earlier device commands of a failed scene stay applied.
        // What it does guarantee: the failure surfaces to the caller, no scene ACK is published
        // (so it is never reported as success), and the apply lock is released for later commands.
        var driver = new RecordingDriver(failOnCallNumber: 3);
        var (bus, _) = await StartGatewayAsync(driver);
        var acks = await MessageRecorder.AttachAsync(bus, RoomTopics.SceneSetAck(Room));

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(bus, Scene(SceneId.Sleep)));
        Assert.Empty(acks.Messages);

        var followUp = new TvSetCommand
        {
            CommandId = "after-failure",
            RoomId = Room,
            DeviceId = "tv-1",
            IssuedAtUtc = DateTimeOffset.UtcNow,
            Volume = 7
        };
        var deviceAcks = await MessageRecorder.AttachAsync(bus, RoomTopics.DeviceSetAck(Room, "tv-1"));
        await bus.PublishAsync(RoomTopics.DeviceSet(Room, "tv-1"), JsonSerializer.Serialize<DeviceCommand>(followUp), ct: TestContext.Current.CancellationToken)
            .WaitAsync(TestSupport.WaitLimit, TestContext.Current.CancellationToken);

        var ack = Assert.Single(deviceAcks.Decode<CommandAck>());
        Assert.Equal("after-failure", ack.CommandId);
        Assert.True(ack.Success);
    }

    [Fact]
    public async Task Command_for_unknown_device_gets_a_failure_ack_instead_of_throwing()
    {
        var (bus, _) = await StartGatewayAsync(new SimulatedDeviceDriver());
        var command = new TvSetCommand
        {
            CommandId = "unknown-device",
            RoomId = Room,
            DeviceId = "tv-99",
            IssuedAtUtc = DateTimeOffset.UtcNow,
            Volume = 3
        };
        var acks = await MessageRecorder.AttachAsync(bus, RoomTopics.DeviceSetAck(Room, "tv-99"));

        await bus.PublishAsync(RoomTopics.DeviceSet(Room, "tv-99"), JsonSerializer.Serialize<DeviceCommand>(command), ct: TestContext.Current.CancellationToken)
            .WaitAsync(TestSupport.WaitLimit, TestContext.Current.CancellationToken);

        var ack = Assert.Single(acks.Decode<CommandAck>());
        Assert.Equal("unknown-device", ack.CommandId);
        Assert.False(ack.Success);
    }

    [Fact]
    public async Task Reused_command_id_on_another_device_is_applied_not_answered_from_the_other_devices_cache()
    {
        var (bus, gateway) = await StartGatewayAsync(new SimulatedDeviceDriver());
        var mainAcks = await MessageRecorder.AttachAsync(bus, RoomTopics.DeviceSetAck(Room, "light-main"));
        var bedAcks = await MessageRecorder.AttachAsync(bus, RoomTopics.DeviceSetAck(Room, "light-bed"));

        LightSetCommand Light(string deviceId) => new()
        {
            CommandId = "shared-id",
            RoomId = Room,
            DeviceId = deviceId,
            IssuedAtUtc = DateTimeOffset.UtcNow,
            IsOn = true,
            BrightnessPercent = 40
        };

        await bus.PublishAsync(RoomTopics.DeviceSet(Room, "light-main"), JsonSerializer.Serialize<DeviceCommand>(Light("light-main")), ct: TestContext.Current.CancellationToken);
        await bus.PublishAsync(RoomTopics.DeviceSet(Room, "light-bed"), JsonSerializer.Serialize<DeviceCommand>(Light("light-bed")), ct: TestContext.Current.CancellationToken);

        var bedAck = Assert.Single(bedAcks.Decode<CommandAck>());
        Assert.True(bedAck.Success);
        Assert.False(bedAck.WasDuplicate);
        Assert.True(gateway.CurrentDeviceStates["light-bed"].IsOn);

        // A genuine redelivery on the same device is still deduplicated.
        await bus.PublishAsync(RoomTopics.DeviceSet(Room, "light-main"), JsonSerializer.Serialize<DeviceCommand>(Light("light-main")), ct: TestContext.Current.CancellationToken);
        Assert.True(mainAcks.Decode<CommandAck>().Last().WasDuplicate);
    }
}
