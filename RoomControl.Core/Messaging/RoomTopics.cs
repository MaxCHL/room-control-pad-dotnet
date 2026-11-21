namespace RoomControl.Core.Messaging;

/// <summary>
/// Centralized MQTT-style topic layout for the whole simulation. Keeping every topic string
/// (and its wildcard subscription variant) in one place avoids typo drift between publishers
/// and subscribers, the same discipline a real MQTT deployment needs.
/// </summary>
public static class RoomTopics
{
    // --- Per-device state / control ---
    public static string DeviceState(string roomId, string deviceId) => $"room/{roomId}/device/{deviceId}/state";
    public static string DeviceSet(string roomId, string deviceId) => $"room/{roomId}/device/{deviceId}/set";
    public static string DeviceSetAck(string roomId, string deviceId) => $"room/{roomId}/device/{deviceId}/set/ack";
    public static string AllDeviceStatesInRoom(string roomId) => $"room/{roomId}/device/+/state";
    public static string AllDeviceSetsInRoom(string roomId) => $"room/{roomId}/device/+/set";
    public static string AllDeviceSetAcksInRoom(string roomId) => $"room/{roomId}/device/+/set/ack";

    // --- Scenes ---
    public static string SceneSet(string roomId) => $"room/{roomId}/scene/set";
    public static string SceneSetAck(string roomId) => $"room/{roomId}/scene/set/ack";

    // --- Room environment ---
    public static string EnvState(string roomId) => $"room/{roomId}/env/state";

    // --- Gateway liveness (retained + Last Will) ---
    public static string GatewayStatus(string roomId) => $"room/{roomId}/gateway/status";

    // --- Guest services (food orders, housekeeping, etc.) ---
    public static string ServiceOrder(string roomId) => $"room/{roomId}/service/order";
    public static string ServiceOrderWildcard => "room/+/service/order";
    public static string ServiceNotify(string roomId) => $"room/{roomId}/service/notify";

    /// <summary>Room id from a <c>room/{roomId}/...</c> topic, or null for any other topic. Services use it
    /// to check that a request's RoomId matches the topic it arrived on (a room may only act for itself).</summary>
    public static string? RoomIdOf(string topic)
    {
        var parts = topic.Split('/');
        return parts.Length >= 3 && parts[0] == "room" && parts[1].Length > 0 ? parts[1] : null;
    }

    // --- Delivery robots ---
    public static string RobotStatus(string robotId) => $"robot/{robotId}/status";
    public static string RobotStatusWildcard => "robot/+/status";

    // --- Hotel-wide facilities ---
    public static string FacilityOccupancy(string facilityId) => $"hotel/facility/{facilityId}/occupancy";

    // --- Restaurant queue ---
    public static string RestaurantQueue(string restaurantId) => $"hotel/restaurant/{restaurantId}/queue";
}
