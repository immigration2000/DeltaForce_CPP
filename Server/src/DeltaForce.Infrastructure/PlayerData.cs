using System.Data;
using System.Globalization;
using DeltaForce.Application;
using Microsoft.EntityFrameworkCore;

namespace DeltaForce.Infrastructure;

public sealed class BlackSiteRow
{
    public Guid PlayerId { get; set; }
    public int StashLevel { get; set; }
    public int CommandCenterLevel { get; set; }
    public int TrainingCenterLevel { get; set; }
    public int FiringRangeLevel { get; set; }
    public int DivingCenterLevel { get; set; }
    public int CollectionRoomLevel { get; set; }
    public int DecontaminationRoomLevel { get; set; }
}

public sealed class CraftingJobRow
{
    public Guid JobId { get; set; }
    public Guid PlayerId { get; set; }
    public string FacilityId { get; set; } = "";
    public string RecipeId { get; set; } = "";
    public int Quantity { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletesAt { get; set; }
    public CraftingStatus Status { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
}

public sealed class PlayerRuntimeRow
{
    public Guid PlayerId { get; set; }
    // Assignment is reserved for the future server-owned party/raid lifecycle.
    public Guid? CurrentPartyId { get; set; }
    public Guid? RaidId { get; set; }
    public PlayerRaidStatus RaidStatus { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal static class PlayerDataModel
{
    public static void Configure(ModelBuilder model)
    {
        var blackSite = model.Entity<BlackSiteRow>();
        blackSite.ToTable("BlackSites", t => t.HasCheckConstraint("CK_BlackSites_Levels",
            "[StashLevel] >= 0 AND [CommandCenterLevel] >= 0 AND [TrainingCenterLevel] >= 0 AND " +
            "[FiringRangeLevel] >= 0 AND [DivingCenterLevel] >= 0 AND [CollectionRoomLevel] >= 0 AND [DecontaminationRoomLevel] >= 0"));
        blackSite.HasKey(b => b.PlayerId);
        blackSite.HasOne<PlayerRow>().WithOne().HasForeignKey<BlackSiteRow>(b => b.PlayerId).OnDelete(DeleteBehavior.Restrict);
        var runtime = model.Entity<PlayerRuntimeRow>();
        runtime.ToTable("PlayerRuntime", t => t.HasCheckConstraint("CK_PlayerRuntime_Raid",
            "([RaidStatus] = N'None' AND [RaidId] IS NULL) OR " +
            "([RaidStatus] IN (N'Preparing', N'Joining', N'InRaid', N'Finished') AND [RaidId] IS NOT NULL)"));
        runtime.HasKey(r => r.PlayerId);
        runtime.Property(r => r.RaidStatus).HasConversion<string>().HasMaxLength(32);
        runtime.HasOne<PlayerRow>().WithOne().HasForeignKey<PlayerRuntimeRow>(r => r.PlayerId).OnDelete(DeleteBehavior.Restrict);
        var job = model.Entity<CraftingJobRow>();
        job.ToTable("CraftingJobs", t =>
        {
            t.HasCheckConstraint("CK_CraftingJobs_Values", "[Quantity] > 0 AND [CompletesAt] >= [StartedAt]");
            t.HasCheckConstraint("CK_CraftingJobs_Status",
                "([Status] IN (N'InProgress', N'ReadyToClaim', N'Cancelled') AND [ClaimedAt] IS NULL) OR " +
                "([Status] = N'Claimed' AND [ClaimedAt] IS NOT NULL AND [ClaimedAt] >= [CompletesAt])");
        });
        job.HasKey(j => j.JobId);
        job.Property(j => j.FacilityId).HasMaxLength(64);
        job.Property(j => j.RecipeId).HasMaxLength(128);
        job.Property(j => j.Status).HasConversion<string>().HasMaxLength(32);
        job.HasIndex(j => new { j.PlayerId, j.Status, j.CompletesAt });
        job.HasOne<PlayerRow>().WithMany().HasForeignKey(j => j.PlayerId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PlayerDataService(InventoryDbContext db, DeltaForce.Domain.DefinitionCatalog catalog,
    InventoryService inventory) : IPlayerDataService
{
    public async Task<PlayerDataSnapshot?> GetAsync(Guid playerId, LoginSessionView session, CancellationToken ct)
    {
        // Same lock as inventory/matching writers: all persisted sections describe one commit boundary.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var player = await db.Players.FromSqlInterpolated(
            $"SELECT * FROM [Players] WITH (UPDLOCK, HOLDLOCK) WHERE [PlayerId] = {playerId}")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (player == null) return null;
        var blackSite = await db.BlackSites.AsNoTracking().SingleAsync(b => b.PlayerId == playerId, ct);
        var runtime = await db.PlayerRuntime.AsNoTracking().SingleAsync(r => r.PlayerId == playerId, ct);
        var state = await inventory.LoadAsync(playerId, ct);
        var entry = await db.RaidEntries.AsNoTracking().SingleOrDefaultAsync(e => e.PlayerId == playerId && e.IsActive, ct);
        var jobs = await db.CraftingJobs.AsNoTracking().Where(j => j.PlayerId == playerId &&
            (j.Status == CraftingStatus.InProgress || j.Status == CraftingStatus.ReadyToClaim))
            .OrderBy(j => j.CompletesAt).ThenBy(j => j.JobId).ToArrayAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var result = new PlayerDataSnapshot(1,
            new(player.PlayerId, player.Nickname, player.Level, player.Experience.ToString(CultureInfo.InvariantCulture),
                player.AvatarId, player.DogtagImageId),
            new(blackSite.StashLevel, blackSite.CommandCenterLevel, blackSite.TrainingCenterLevel,
                blackSite.FiringRangeLevel, blackSite.DivingCenterLevel, blackSite.CollectionRoomLevel, blackSite.DecontaminationRoomLevel),
            jobs.Select(j => new CraftingJobView(j.JobId, j.FacilityId, j.RecipeId, j.Quantity, j.StartedAt, j.CompletesAt,
                j.Status == CraftingStatus.InProgress && j.CompletesAt <= now ? CraftingStatus.ReadyToClaim : j.Status)).ToArray(),
            InventoryViews.Snapshot(state, player.Revision, catalog),
            new(session, runtime.CurrentPartyId, new(entry == null ? PlayerMatchingStatus.Idle : PlayerMatchingStatus.Matching,
                entry == null ? null : new(entry.RaidId, entry.EntryId, entry.MapId, entry.Status, entry.CreatedAt)),
                new(runtime.RaidStatus, runtime.RaidId)));
        await transaction.CommitAsync(ct);
        return result;
    }
}
