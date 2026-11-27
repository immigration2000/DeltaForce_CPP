using DeltaForce.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeltaForce.Infrastructure;

public sealed class PlayerRow
{
    public Guid PlayerId { get; set; }
    public string UserId { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public long Revision { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
public sealed class ItemRow
{
    public Guid ItemId { get; set; }
    public Guid PlayerId { get; set; }
    public string DefinitionId { get; set; } = "";
    public int Quantity { get; set; }
    public double CurrentDurability { get; set; }
    public LocationRow Location { get; set; } = null!;
}
public sealed class LocationRow
{
    public Guid ItemId { get; set; }
    public Guid PlayerId { get; set; }
    public LocationType Type { get; set; }
    public Guid? ContainerId { get; set; }
    public int GridIndex { get; set; } = -1;
    public int X { get; set; }
    public int Y { get; set; }
    public bool Rotated { get; set; }
    public string? SlotTag { get; set; }
    public InventoryLocation ToDomain() => new(Type, ContainerId, GridIndex, new(X, Y), Rotated, SlotTag);
    public void Apply(InventoryLocation location)
    {
        location = location.Canonical();
        Type = location.Type; ContainerId = location.ContainerId; GridIndex = location.GridIndex;
        X = location.CellPosition?.X ?? 0; Y = location.CellPosition?.Y ?? 0;
        Rotated = location.Rotated; SlotTag = location.SlotTag;
    }
}
public sealed class ContainerRow
{
    public Guid ContainerId { get; set; }
    public Guid PlayerId { get; set; }
    public Guid? OwnerItemId { get; set; }
    public string LayoutId { get; set; } = "";
    public string StorageTag { get; set; } = "";
    public int Level { get; set; }
}
public sealed class OperationRow
{
    public Guid PlayerId { get; set; }
    public Guid RequestId { get; set; }
    public string PayloadHash { get; set; } = "";
    public string ResponseJson { get; set; } = "";
    public int StatusCode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public sealed class RetiredItemRow
{
    public Guid ItemId { get; set; }
    public Guid PlayerId { get; set; }
    public Guid RequestId { get; set; }
}

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<PlayerRow> Players => Set<PlayerRow>();
    public DbSet<ItemRow> Items => Set<ItemRow>();
    public DbSet<LocationRow> Locations => Set<LocationRow>();
    public DbSet<ContainerRow> Containers => Set<ContainerRow>();
    public DbSet<OperationRow> Operations => Set<OperationRow>();
    public DbSet<RetiredItemRow> RetiredItems => Set<RetiredItemRow>();
    public DbSet<RaidEntryRow> RaidEntries => Set<RaidEntryRow>();
    public DbSet<RaidRequestRow> RaidRequests => Set<RaidRequestRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var player = model.Entity<PlayerRow>();
        player.ToTable("Players", t => t.HasCheckConstraint("CK_Players_Revision", "[Revision] >= 1"));
        player.HasKey(p => p.PlayerId);
        player.Property(p => p.UserId).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2");
        player.HasIndex(p => p.UserId).IsUnique();
        player.Property(p => p.PasswordHash).HasMaxLength(512);
        player.Property(p => p.Revision).IsConcurrencyToken();
        var item = model.Entity<ItemRow>();
        item.ToTable("ItemInstances", t => t.HasCheckConstraint("CK_Items_Values", "[Quantity] > 0 AND [CurrentDurability] >= 0"));
        item.HasKey(i => i.ItemId);
        item.HasAlternateKey(i => new { i.PlayerId, i.ItemId });
        item.Property(i => i.DefinitionId).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        item.HasOne<PlayerRow>().WithMany().HasForeignKey(i => i.PlayerId).OnDelete(DeleteBehavior.Restrict);
        var container = model.Entity<ContainerRow>();
        container.ToTable("Containers");
        container.HasKey(c => c.ContainerId);
        container.HasAlternateKey(c => new { c.PlayerId, c.ContainerId });
        container.Property(c => c.LayoutId).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        container.Property(c => c.StorageTag).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        container.HasOne<PlayerRow>().WithMany().HasForeignKey(c => c.PlayerId).OnDelete(DeleteBehavior.Restrict);
        container.HasOne<ItemRow>().WithMany().HasForeignKey(c => new { c.PlayerId, c.OwnerItemId })
            .HasPrincipalKey(i => new { i.PlayerId, i.ItemId }).OnDelete(DeleteBehavior.Restrict);
        container.HasIndex(c => c.OwnerItemId).IsUnique().HasFilter("[OwnerItemId] IS NOT NULL");
        var location = model.Entity<LocationRow>();
        location.ToTable("ItemLocations", t => t.HasCheckConstraint("CK_Location_Shape",
            "([Type] = 0 AND [ContainerId] IS NULL AND [SlotTag] IS NULL) OR " +
            "([Type] = 1 AND [ContainerId] IS NOT NULL AND [GridIndex] >= 0 AND [X] >= 0 AND [Y] >= 0 AND [SlotTag] IS NULL) OR " +
            "([Type] = 2 AND [ContainerId] IS NULL AND [GridIndex] = -1 AND [X] = 0 AND [Y] = 0 AND [Rotated] = 0 AND [SlotTag] IS NOT NULL)"));
        location.HasKey(l => l.ItemId);
        location.Property(l => l.SlotTag).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        item.HasOne(i => i.Location).WithOne().HasForeignKey<LocationRow>(l => new { l.PlayerId, l.ItemId })
            .HasPrincipalKey<ItemRow>(i => new { i.PlayerId, i.ItemId }).OnDelete(DeleteBehavior.Cascade);
        location.HasOne<ContainerRow>().WithMany().HasForeignKey(l => new { l.PlayerId, l.ContainerId })
            .HasPrincipalKey(c => new { c.PlayerId, c.ContainerId }).OnDelete(DeleteBehavior.Restrict);
        location.HasIndex(l => new { l.PlayerId, l.SlotTag }).IsUnique().HasFilter("[Type] = 2");
        var operation = model.Entity<OperationRow>();
        operation.ToTable("OperationRecords");
        operation.HasKey(o => new { o.PlayerId, o.RequestId });
        operation.Property(o => o.PayloadHash).HasMaxLength(64).IsUnicode(false);
        operation.HasOne<PlayerRow>().WithMany().HasForeignKey(o => o.PlayerId).OnDelete(DeleteBehavior.Restrict);
        var retired = model.Entity<RetiredItemRow>();
        retired.ToTable("RetiredItems");
        retired.HasKey(r => r.ItemId);
        retired.HasOne<PlayerRow>().WithMany().HasForeignKey(r => r.PlayerId).OnDelete(DeleteBehavior.Restrict);
        var entry = model.Entity<RaidEntryRow>();
        entry.ToTable("RaidEntries", t => t.HasCheckConstraint("CK_RaidEntries_State",
            "([Status] = N'Reserved' AND [IsActive] = 1) OR ([Status] = N'Cancelled' AND [IsActive] = 0)"));
        entry.HasKey(e => e.EntryId);
        entry.Property(e => e.MapId).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2");
        entry.Property(e => e.Status).HasMaxLength(32);
        entry.HasIndex(e => new { e.PlayerId, e.RaidId }).IsUnique();
        entry.HasIndex(e => e.PlayerId).IsUnique().HasFilter("[IsActive] = 1");
        entry.HasOne<PlayerRow>().WithMany().HasForeignKey(e => e.PlayerId).OnDelete(DeleteBehavior.Restrict);
        var raidRequest = model.Entity<RaidRequestRow>();
        raidRequest.ToTable("RaidRequestRecords");
        raidRequest.HasKey(r => new { r.PlayerId, r.RequestId });
        raidRequest.Property(r => r.PayloadHash).HasMaxLength(64).IsUnicode(false);
        raidRequest.HasOne<PlayerRow>().WithMany().HasForeignKey(r => r.PlayerId).OnDelete(DeleteBehavior.Restrict);
    }
}
