namespace RoomControl.Core.Messaging;

/// <summary>
/// In-process abstraction that mimics the subset of MQTT broker semantics this simulation
/// relies on: hierarchical topics, retained messages, Last Will and Testament, and simple
/// pub/sub fan-out. It intentionally has no network code — in a production deployment this
/// interface would be implemented on top of a real MQTT client (e.g. MQTTnet) talking to a
/// real broker; the room gateway, Pad and backend services only ever depend on this
/// interface, so swapping the in-memory bus for a real broker client is a one-file change.
/// </summary>
public interface IRoomMessageBus
{
    /// <summary>
    /// Publishes a message to a topic. When <paramref name="retain"/> is true, the broker
    /// keeps the last payload for that exact topic and immediately delivers it to any future
    /// subscriber whose filter matches (mirrors MQTT retained messages) — this is how a Pad
    /// that (re)connects instantly learns the current device/gateway/env state without
    /// waiting for the next change.
    /// </summary>
    Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to a topic filter (supports MQTT-style '+' single-level and '#'
    /// multi-level trailing wildcards). Any retained message currently matching the filter
    /// is delivered to the handler before this call returns. Dispose the result to unsubscribe.
    /// </summary>
    Task<IDisposable> SubscribeAsync(string topicFilter, Func<string, string, CancellationToken, Task> handler, CancellationToken ct = default);

    /// <summary>
    /// Registers a Last Will and Testament for a client: if that client later disconnects
    /// ungracefully (<see cref="DisconnectUngracefullyAsync"/>), the broker publishes this
    /// message on the client's behalf. This is how offline detection works for a room
    /// gateway that crashes or loses network without a chance to say goodbye.
    /// </summary>
    void RegisterLastWill(string clientId, string topic, string payload, bool retain = true);

    /// <summary>Marks a client as connected, clearing any previous will trigger.</summary>
    void Connect(string clientId);

    /// <summary>Simulates an ungraceful disconnect (process crash / network loss): fires the
    /// registered Last Will message for this client, if one was registered.</summary>
    Task DisconnectUngracefullyAsync(string clientId, CancellationToken ct = default);

    /// <summary>Simulates a graceful disconnect: no Last Will is fired.</summary>
    void DisconnectGracefully(string clientId);
}
