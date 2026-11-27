using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaForce.Application;
using DeltaForce.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeltaForce.Infrastructure;

public sealed class InventoryService(InventoryDbContext db, DefinitionCatalog catalog, InventoryEngine engine) : IInventoryService
{
    public async Task<InventorySnapshot?> GetAsync(Guid playerId, CancellationToken ct)
    {
        // Use the same scope lock as writers. A shared player lock could deadlock with
        // a writer holding item locks while upgrading its player lock at commit.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var player = await db.Players.FromSqlInterpolated(
            $"SELECT * FROM [Players] WITH (UPDLOCK, HOLDLOCK) WHERE [PlayerId] = {playerId}")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (player == null) return null;
        var state = await LoadAsync(playerId, ct);
        var snapshot = InventoryViews.Snapshot(state, player.Revision, catalog);
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    public async Task<OperationResponse?> GetResultAsync(Guid playerId, Guid requestId, CancellationToken ct)
    {
        var record = await db.Operations.AsNoTracking().SingleOrDefaultAsync(o => o.PlayerId == playerId && o.RequestId == requestId, ct);
        return record == null ? null : Read(record);
    }

    public async Task<OperationResponse> ExecuteAsync(Guid playerId, InventoryCommand command, CancellationToken ct)
    {
        if (command.RequestId == Guid.Empty || command.ItemId == Guid.Empty || command.Source == null
            || command.Destination == null || !command.Source.IsValid() || !command.Destination.IsValid()
            || command.ExpectedRevisions is not { Length: 1 } || command.ExpectedRevisions[0] == null
            || command.ExpectedRevisions[0].ScopeId != InventoryViews.Scope
            || !long.TryParse(command.ExpectedRevisions[0].Revision, NumberStyles.None, CultureInfo.InvariantCulture, out long expected)
            || expected < 1 || string.IsNullOrEmpty(command.DefinitionVersion))
            return Rejected(command.RequestId, "InvalidRequest", 400);

        command = command with { Source = command.Source.Canonical(), Destination = command.Destination.Canonical(),
            ExpectedRevisions = [new(InventoryViews.Scope, expected.ToString(CultureInfo.InvariantCulture))] };
        string hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, ContractJson.Options)));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // SQL lock, not an in-process mutex: serializes every writer for this player's scope,
        // including concurrent requests served by different API processes.
        var player = await db.Players.FromSqlInterpolated(
            $"SELECT * FROM [Players] WITH (UPDLOCK, HOLDLOCK) WHERE [PlayerId] = {playerId}")
            .SingleOrDefaultAsync(ct);
        if (player == null) return Rejected(command.RequestId, "PlayerNotFound", 404);
        var existing = await db.Operations.SingleOrDefaultAsync(o => o.PlayerId == playerId && o.RequestId == command.RequestId, ct);
        if (existing != null)
            return existing.PayloadHash == hash ? Read(existing) : Rejected(command.RequestId, "RequestPayloadMismatch", 409);

        OperationResponse response;
        if (command.DefinitionVersion != catalog.Version)
            response = Rejected(command.RequestId, "DefinitionMismatch", 409, player.Revision);
        else if (player.Revision != expected)
            response = Rejected(command.RequestId, "RevisionMismatch", 409, player.Revision);
        else
        {
            var before = await LoadAsync(playerId, ct);
            try
            {
                var mutation = engine.Execute(before, new(command.Operation, command.ItemId, command.Source,
                    command.Destination, command.TargetItemId, command.Quantity));
                var changed = mutation.UpdatedItems.Select(i => i.ItemId).Concat(mutation.RemovedItemIds).ToArray();
                // SQL Server unique slot indexes are immediate. Vacate affected locations
                // inside this transaction before an equipment-to-equipment swap.
                await db.Locations.Where(l => changed.Contains(l.ItemId) && l.PlayerId == playerId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.Type, LocationType.Unplaced)
                        .SetProperty(l => l.ContainerId, (Guid?)null).SetProperty(l => l.SlotTag, (string?)null), ct);
                foreach (var item in mutation.UpdatedItems)
                {
                    var row = db.Items.Local.Single(i => i.ItemId == item.ItemId);
                    row.Quantity = item.Quantity; row.CurrentDurability = item.CurrentDurability;
                    row.Location.Apply(item.Location);
                    db.Entry(row.Location).State = EntityState.Modified;
                }
                foreach (var item in mutation.AddedItems)
                {
                    if (await db.RetiredItems.AnyAsync(r => r.ItemId == item.ItemId, ct))
                        throw new InvalidOperationException("Generated ID was previously retired");
                    db.Items.Add(ToRow(item, playerId));
                }
                foreach (var id in mutation.RemovedItemIds)
                {
                    db.Items.Remove(db.Items.Local.Single(i => i.ItemId == id));
                    db.RetiredItems.Add(new() { ItemId = id, PlayerId = playerId, RequestId = command.RequestId });
                }
                long baseRevision = player.Revision;
                player.Revision = checked(player.Revision + 1);
                player.UpdatedAt = DateTime.UtcNow;
                var oldSlots = InventoryViews.Slots(before, catalog).ToDictionary(s => s.SlotTag);
                var slots = InventoryViews.Slots(mutation.State, catalog).Where(s => s != oldSlots[s.SlotTag]).ToArray();
                var containers = mutation.State.Containers.Values.Where(c =>
                    InventoryEngine.IsAccessible(before, c) != InventoryEngine.IsAccessible(mutation.State, c))
                    .Select(c => InventoryViews.Container(mutation.State, c, catalog)).ToArray();
                string from = baseRevision.ToString(CultureInfo.InvariantCulture), to = player.Revision.ToString(CultureInfo.InvariantCulture);
                response = new(200, new(command.RequestId, "Applied", null, false,
                    [new(InventoryViews.Scope, from)], [new(InventoryViews.Scope, to)],
                    new(Guid.NewGuid(), [new(InventoryViews.Scope, from, to,
                        mutation.AddedItems.Select(i => InventoryViews.Item(i, catalog)).ToArray(),
                        mutation.UpdatedItems.Select(i => InventoryViews.Item(i, catalog)).ToArray(),
                        mutation.RemovedItemIds, containers, slots)])));
            }
            catch (InventoryRuleException e)
            {
                response = Rejected(command.RequestId, e.Code, ErrorStatus(e.Code), player.Revision);
            }
        }
        db.Operations.Add(new() { PlayerId = playerId, RequestId = command.RequestId, PayloadHash = hash,
            StatusCode = response.StatusCode, ResponseJson = JsonSerializer.Serialize(response.Result, ContractJson.Options) });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return response;
    }

    internal async Task<InventoryState> LoadAsync(Guid playerId, CancellationToken ct)
    {
        var items = await db.Items.Include(i => i.Location).Where(i => i.PlayerId == playerId).ToListAsync(ct);
        var containers = await db.Containers.AsNoTracking().Where(c => c.PlayerId == playerId).ToListAsync(ct);
        return new(items.ToDictionary(i => i.ItemId, i => new InventoryItem(i.ItemId, i.DefinitionId,
                i.Quantity, i.CurrentDurability, i.Location.ToDomain())),
            containers.ToDictionary(c => c.ContainerId, c => new InventoryContainer(c.ContainerId,
                c.OwnerItemId, c.LayoutId, c.StorageTag, c.Level)));
    }
    public static ItemRow ToRow(InventoryItem item, Guid playerId)
    {
        var location = new LocationRow { ItemId = item.ItemId, PlayerId = playerId };
        location.Apply(item.Location);
        return new() { ItemId = item.ItemId, PlayerId = playerId, DefinitionId = item.DefinitionId,
            Quantity = item.Quantity, CurrentDurability = item.CurrentDurability, Location = location };
    }
    private static OperationResponse Read(OperationRow record)
        => new(record.StatusCode, JsonSerializer.Deserialize<OperationResult>(record.ResponseJson, ContractJson.Options)!);
    private static OperationResponse Rejected(Guid id, string error, int status, long? revision = null)
    {
        ScopeRevision[] revisions = revision.HasValue ? [new(InventoryViews.Scope, revision.Value.ToString(CultureInfo.InvariantCulture))] : [];
        return new(status, new(id, "Rejected", error, false, revisions, revisions, null));
    }
    private static int ErrorStatus(string code) => code switch
    {
        "ItemNotFound" or "ContainerNotFound" => 404,
        "ContainerAccessDenied" => 403,
        "SourceMismatch" => 409,
        "InvalidRequest" or "InvalidLocation" => 400,
        _ => 422
    };
}
