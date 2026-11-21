namespace RoomControl.Core.Models;

/// <summary>
/// In-room environment sensor reading, published (retained) on <c>room/{roomId}/env/state</c>.
/// In production this would be aggregated by the gateway from Zigbee sensor reports
/// (temperature/humidity/CO2/PM2.5 sensor clusters); here it is just simulated data.
/// </summary>
public sealed record RoomEnvState
{
    public required string RoomId { get; init; }
    public required double TemperatureC { get; init; }
    public required double HumidityPercent { get; init; }
    public required int Co2Ppm { get; init; }
    public required double Pm25UgM3 { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
