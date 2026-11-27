using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaForce.Application;
using Microsoft.EntityFrameworkCore;

namespace DeltaForce.Infrastructure;

public sealed class RaidEntryRow
{
    public Guid EntryId { get; set; }
    public Guid RaidId { get; set; }
    public Guid PlayerId { get; set; }
    public string MapId { get; set; } = "";
    public string Status { get; set; } = "Reserved";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class RaidRequestRow
{
    public Guid PlayerId { get; set; }
    public Guid RequestId { get; set; }
    public string PayloadHash { get; set; } = "";
    public string ResponseJson { get; set; } = "";
    public int StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RaidReservationService(InventoryDbContext db, RaidMaps maps) : IRaidReservationService
{
    public async Task<RaidResponse> ReserveAsync(Guid playerId, ReserveRaidRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.MapId) || request.MapId.Length > 64)
            return Error(400, "InvalidRequest");
        string hash = Hash(new { action = "Reserve", request.MapId });
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await LockPlayer(playerId, ct)) return Error(404, "PlayerNotFound");
        var replay = await Replay(playerId, request.RequestId, hash, ct);
        if (replay != null) return replay;
        RaidResponse result;
        if (!maps.Allowed.Contains(request.MapId)) result = Error(400, "InvalidMap");
        else if (await db.RaidEntries.AnyAsync(e => e.PlayerId == playerId && e.IsActive, ct))
            result = Error(409, "AlreadyMatching");
        else
        {
            var entry = new RaidEntryRow { PlayerId = playerId, EntryId = Guid.NewGuid(), RaidId = Guid.NewGuid(), MapId = request.MapId };
            db.RaidEntries.Add(entry);
            result = new(201, View(entry));
        }
        await Persist(playerId, request.RequestId, hash, result, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<RaidResponse> CancelAsync(Guid playerId, Guid raidId, CancelRaidRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || request.EntryId == Guid.Empty || raidId == Guid.Empty)
            return Error(400, "InvalidRequest");
        string hash = Hash(new { action = "Cancel", raidId, request.EntryId });
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await LockPlayer(playerId, ct)) return Error(404, "PlayerNotFound");
        var replay = await Replay(playerId, request.RequestId, hash, ct);
        if (replay != null) return replay;
        var entry = await db.RaidEntries.SingleOrDefaultAsync(e => e.PlayerId == playerId && e.RaidId == raidId && e.EntryId == request.EntryId, ct);
        RaidResponse result;
        if (entry == null) result = Error(404, "EntryNotFound");
        else if (entry.Status is not ("Reserved" or "Cancelled")) result = Error(409, "EntryNotCancelable");
        else
        {
            entry.Status = "Cancelled";
            entry.IsActive = false;
            result = new(200, View(entry));
        }
        await Persist(playerId, request.RequestId, hash, result, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<RaidReservation?> GetCurrentAsync(Guid playerId, CancellationToken ct)
    {
        var entry = await db.RaidEntries.AsNoTracking().SingleOrDefaultAsync(e => e.PlayerId == playerId && e.IsActive, ct);
        return entry == null ? null : View(entry);
    }
    public async Task<RaidReservation?> GetEntryAsync(Guid playerId, Guid raidId, CancellationToken ct)
    {
        var entry = await db.RaidEntries.AsNoTracking().SingleOrDefaultAsync(e => e.PlayerId == playerId && e.RaidId == raidId, ct);
        return entry == null ? null : View(entry);
    }
    public async Task<RaidResponse?> GetResultAsync(Guid playerId, Guid requestId, CancellationToken ct)
    {
        var row = await db.RaidRequests.AsNoTracking().SingleOrDefaultAsync(r => r.PlayerId == playerId && r.RequestId == requestId, ct);
        return row == null ? null : Read(row);
    }

    private async Task<bool> LockPlayer(Guid playerId, CancellationToken ct)
        => await db.Players.FromSqlInterpolated($"SELECT * FROM [Players] WITH (UPDLOCK, HOLDLOCK) WHERE [PlayerId] = {playerId}")
            .SingleOrDefaultAsync(ct) != null;
    private async Task<RaidResponse?> Replay(Guid playerId, Guid requestId, string hash, CancellationToken ct)
    {
        var row = await db.RaidRequests.SingleOrDefaultAsync(r => r.PlayerId == playerId && r.RequestId == requestId, ct);
        return row == null ? null : row.PayloadHash == hash ? Read(row) : Error(409, "RequestPayloadMismatch");
    }
    private async Task Persist(Guid playerId, Guid requestId, string hash, RaidResponse result, CancellationToken ct)
    {
        // Both saves belong to the caller's transaction. Failure to store the durable
        // response rolls back the reservation/cancellation as well.
        await db.SaveChangesAsync(ct);
        db.RaidRequests.Add(new() { PlayerId = playerId, RequestId = requestId, PayloadHash = hash,
            ResponseJson = JsonSerializer.Serialize(result, ContractJson.Options), StatusCode = result.StatusCode });
        await db.SaveChangesAsync(ct);
    }
    private static RaidReservation View(RaidEntryRow entry) => new(entry.RaidId, entry.EntryId, entry.MapId, entry.Status, entry.CreatedAt);
    private static RaidResponse Read(RaidRequestRow row) => JsonSerializer.Deserialize<RaidResponse>(row.ResponseJson, ContractJson.Options)!;
    private static RaidResponse Error(int status, string code) => new(status, null, new(code));
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options)));
}
