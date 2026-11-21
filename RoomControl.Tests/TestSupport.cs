using System.Collections.Concurrent;
using System.Text.Json;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;

namespace RoomControl.Tests;

internal static class TestSupport
{
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    public static RoomDeviceInventory Inventory(string roomId = "R101") => new()
    {
        RoomId = roomId,
        LightZoneDeviceIds = new Dictionary<string, string>
        {
            ["main"] = "light-main",
            ["bed"] = "light-bed",
            ["bath"] = "light-bath",
            ["night"] = "light-night"
        },
        CurtainDeviceId = "curtain-1",
        AirconDeviceId = "aircon-1",
        TvDeviceId = "tv-1",
        DoorSignDeviceId = "doorsign-1"
    };

    /// <summary>Polls a condition with a short delay until it holds or the limit is hit.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(5);
        }
    }
}

/// <summary>Collects every message delivered for a topic filter, in delivery order.</summary>
internal sealed class MessageRecorder
{
    private readonly ConcurrentQueue<(string Topic, string Payload)> _messages = new();

    public static async Task<MessageRecorder> AttachAsync(IRoomMessageBus bus, string filter)
    {
        var recorder = new MessageRecorder();
        await bus.SubscribeAsync(filter, (topic, payload, _) =>
        {
            recorder._messages.Enqueue((topic, payload));
            return Task.CompletedTask;
        });
        return recorder;
    }

    public IReadOnlyList<(string Topic, string Payload)> Messages => _messages.ToArray();

    public IReadOnlyList<T> Decode<T>() =>
        _messages.Select(m => JsonSerializer.Deserialize<T>(m.Payload)!).ToArray();
}
