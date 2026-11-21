using System.Collections.Concurrent;
using RoomControl.Core.Messaging;

namespace RoomControl.Sim;

/// <summary>
/// In-process implementation of <see cref="IRoomMessageBus"/> that reproduces the MQTT
/// semantics this simulation needs: topic wildcards ('+' and '#'), retained messages, and
/// Last Will and Testament. There is no network, no real broker, and no QoS-level packet
/// retransmission — the "QoS1" contract in this demo is honored at the application layer
/// (see <c>SimulatedRoomGateway</c>'s CommandId dedup), exactly as an application must do
/// even against a real broker, since MQTT QoS1 only guarantees at-least-once delivery.
/// </summary>
public sealed class InMemoryMessageBus : IRoomMessageBus
{
    private sealed record Subscription(string Filter, Func<string, string, CancellationToken, Task> Handler);
    private sealed record Will(string Topic, string Payload, bool Retain);

    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, string> _retained = new();
    private readonly ConcurrentDictionary<string, Will> _lastWills = new();

    public async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken ct = default)
    {
        if (retain)
        {
            _retained[topic] = payload;
        }

        List<Subscription> snapshot;
        lock (_gate)
        {
            snapshot = _subscriptions.Where(s => TopicMatches(s.Filter, topic)).ToList();
        }

        foreach (var sub in snapshot)
        {
            await sub.Handler(topic, payload, ct).ConfigureAwait(false);
        }
    }

    public async Task<IDisposable> SubscribeAsync(string topicFilter, Func<string, string, CancellationToken, Task> handler, CancellationToken ct = default)
    {
        var subscription = new Subscription(topicFilter, handler);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        // Mirror MQTT retained-on-subscribe: deliver every currently-retained topic that
        // matches the new filter immediately, before the subscribe call returns.
        foreach (var (topic, payload) in _retained.ToArray())
        {
            if (TopicMatches(topicFilter, topic))
            {
                await handler(topic, payload, ct).ConfigureAwait(false);
            }
        }

        return new Unsubscriber(this, subscription);
    }

    public void RegisterLastWill(string clientId, string topic, string payload, bool retain = true)
        => _lastWills[clientId] = new Will(topic, payload, retain);

    public void Connect(string clientId)
    {
        // A fresh connection supersedes any previously registered will until re-registered.
        _lastWills.TryRemove(clientId, out _);
    }

    public async Task DisconnectUngracefullyAsync(string clientId, CancellationToken ct = default)
    {
        if (_lastWills.TryRemove(clientId, out var will))
        {
            await PublishAsync(will.Topic, will.Payload, will.Retain, ct).ConfigureAwait(false);
        }
    }

    public void DisconnectGracefully(string clientId)
    {
        _lastWills.TryRemove(clientId, out _);
    }

    /// <summary>MQTT-style topic filter matching: '+' matches exactly one level, a trailing
    /// '#' matches that level and everything below it.</summary>
    private static bool TopicMatches(string filter, string topic)
    {
        var filterLevels = filter.Split('/');
        var topicLevels = topic.Split('/');

        for (var i = 0; i < filterLevels.Length; i++)
        {
            if (filterLevels[i] == "#")
            {
                return true; // '#' must be last and matches all remaining levels
            }

            if (i >= topicLevels.Length)
            {
                return false; // filter has more levels than the topic
            }

            if (filterLevels[i] == "+")
            {
                continue;
            }

            if (!string.Equals(filterLevels[i], topicLevels[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return filterLevels.Length == topicLevels.Length;
    }

    private sealed class Unsubscriber(InMemoryMessageBus bus, Subscription subscription) : IDisposable
    {
        public void Dispose()
        {
            lock (bus._gate)
            {
                bus._subscriptions.Remove(subscription);
            }
        }
    }
}
