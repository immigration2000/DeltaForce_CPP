using DeltaForce.Application;
using DeltaForce.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeltaForce.Infrastructure;

public sealed class AccountService(InventoryDbContext db, PasswordHasher<PlayerRow> hasher) : IAccountService
{
    // Verify a dummy hash too, so an unknown account follows the expensive hash path.
    private static readonly PlayerRow Dummy = new();
    private static readonly string DummyHash = new PasswordHasher<PlayerRow>().HashPassword(Dummy, Guid.NewGuid().ToString());
    public async Task<AuthenticatedPlayer?> VerifyAsync(string userId, string password, CancellationToken ct)
    {
        var player = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        var result = hasher.VerifyHashedPassword(player ?? Dummy, player?.PasswordHash ?? DummyHash, password);
        return player != null && result != PasswordVerificationResult.Failed ? new(player.PlayerId, player.UserId) : null;
    }
}

public static class DevelopmentSeeder
{
    public static async Task EnsureAsync(InventoryDbContext db, DefinitionCatalog catalog, string password,
        CancellationToken ct = default, bool resetExistingPasswords = false)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 128)
            throw new InvalidOperationException("Development seed password needs 1-128 characters");
        var hasher = new PasswordHasher<PlayerRow>();
        foreach (string userId in new[] { "demo", "demo2" })
        {
            var existing = await db.Players.SingleOrDefaultAsync(p => p.UserId == userId, ct);
            if (existing != null)
            {
                if (resetExistingPasswords && hasher.VerifyHashedPassword(existing, existing.PasswordHash, password)
                    == PasswordVerificationResult.Failed)
                {
                    existing.PasswordHash = hasher.HashPassword(existing, password);
                    await db.SaveChangesAsync(ct);
                }
                continue;
            }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var player = new PlayerRow { PlayerId = Guid.NewGuid(), UserId = userId };
            player.PasswordHash = hasher.HashPassword(player, password);
            db.Players.Add(player);
            await db.SaveChangesAsync(ct);
            Guid stashId = Guid.NewGuid(), bagId = Guid.NewGuid();
            // IDs refer to the existing UE data export, not client-authored definitions.
            var seeds = new InventoryItem[]
            {
                new(Guid.NewGuid(), "11010006002", 1, 0, InventoryLocation.Grid(stashId, 0, 0)),
                new(Guid.NewGuid(), "11050005001", 1, 0, InventoryLocation.Grid(stashId, 3, 0)),
                new(bagId, "11080001001", 1, 0, InventoryLocation.Grid(stashId, 7, 0)),
                new(Guid.NewGuid(), "37100200001", 30, 0, InventoryLocation.Grid(stashId, 0, 5)),
                new(Guid.NewGuid(), "37100200001", 10, 0, InventoryLocation.Grid(stashId, 1, 5)),
                new(Guid.NewGuid(), "18010000006", 1, 0, InventoryLocation.Grid(stashId, 0, 16)),
                new(Guid.NewGuid(), "18010000006", 1, 0, InventoryLocation.Grid(stashId, 0, 18))
            };
            // Insert item instances before item-owned containers and their locations.
            foreach (var seed in seeds)
                db.Items.Add(new() { ItemId = seed.ItemId, PlayerId = player.PlayerId, DefinitionId = seed.DefinitionId,
                    Quantity = seed.Quantity, CurrentDurability = seed.CurrentDurability });
            await db.SaveChangesAsync(ct);
            var containers = new InventoryContainer[]
            {
                new(stashId, null, "Stash_Main_00", "Storage.Stash.Main"),
                new(bagId, bagId, "Backpack_Sling_11080001001", "Storage.Backpack")
            };
            var state = new InventoryState(seeds.ToDictionary(i => i.ItemId), containers.ToDictionary(c => c.ContainerId));
            new InventoryEngine(catalog).Validate(state);
            foreach (var c in containers) db.Containers.Add(new() { ContainerId = c.ContainerId, PlayerId = player.PlayerId,
                OwnerItemId = c.OwnerItemId, LayoutId = c.LayoutId, StorageTag = c.StorageTag, Level = c.Level });
            await db.SaveChangesAsync(ct);
            foreach (var seed in seeds)
            {
                var row = InventoryService.ToRow(seed, player.PlayerId);
                db.Items.Local.Single(i => i.ItemId == seed.ItemId).Location = row.Location;
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
    }
}
