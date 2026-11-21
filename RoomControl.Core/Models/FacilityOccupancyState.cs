namespace RoomControl.Core.Models;

/// <summary>
/// Real-time occupancy of a hotel facility (gym, pool, business center...), published
/// (retained) on <c>hotel/facility/{facilityId}/occupancy</c> so any Pad can show live status.
/// </summary>
public sealed record FacilityOccupancyState
{
    public required string FacilityId { get; init; }
    public required string DisplayName { get; init; }
    public required int CurrentCount { get; init; }
    public required int Capacity { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>Peak-usage warning threshold: 80% of capacity or more.</summary>
    public bool IsPeak => Capacity > 0 && CurrentCount * 5 >= Capacity * 4;
}
