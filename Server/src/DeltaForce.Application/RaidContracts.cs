namespace DeltaForce.Application;

public sealed record ReserveRaidRequest(Guid RequestId, string MapId);
public sealed record CancelRaidRequest(Guid RequestId, Guid EntryId);
public sealed record RaidReservation(Guid RaidId, Guid EntryId, string MapId, string Status,
    DateTimeOffset CreatedAt);
public sealed record RaidResponse(int StatusCode, RaidReservation? Reservation, ApiError? Error = null);
public sealed class RaidMaps(IEnumerable<string> mapIds)
{
    public IReadOnlySet<string> Allowed { get; } = mapIds.ToHashSet(StringComparer.Ordinal);
}
public interface IRaidReservationService
{
    Task<RaidResponse> ReserveAsync(Guid playerId, ReserveRaidRequest request, CancellationToken ct);
    Task<RaidResponse> CancelAsync(Guid playerId, Guid raidId, CancelRaidRequest request, CancellationToken ct);
    Task<RaidReservation?> GetCurrentAsync(Guid playerId, CancellationToken ct);
    Task<RaidReservation?> GetEntryAsync(Guid playerId, Guid raidId, CancellationToken ct);
    Task<RaidResponse?> GetResultAsync(Guid playerId, Guid requestId, CancellationToken ct);
}
