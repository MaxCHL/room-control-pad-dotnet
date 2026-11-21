namespace RoomControl.Core.Models;

/// <summary>One walk-in queue ticket for a hotel restaurant.</summary>
public sealed record RestaurantQueueTicket
{
    public required string TicketNumber { get; init; }
    public required string RestaurantId { get; init; }
    public required string RoomId { get; init; }
    public required int PartySize { get; init; }
    public required QueueStatus Status { get; init; }
    public required DateTimeOffset IssuedAtUtc { get; init; }
}

/// <summary>
/// Aggregate restaurant queue status, published (retained) on
/// <c>hotel/restaurant/{restId}/queue</c>.
/// </summary>
public sealed record RestaurantQueueState
{
    public required string RestaurantId { get; init; }
    public required string DisplayName { get; init; }
    public required int WaitingCount { get; init; }
    public string? NowCallingTicket { get; init; }
    public required int EstimatedWaitMinutes { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
