using System.Net.Http.Json;
using System.Text.Json;
using DeltaForce.Application;
using DeltaForce.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

internal static class PlayerDataChecks
{
    private static readonly Guid LegacyId = Guid.NewGuid();

    public static async Task PrepareLegacyAsync(string connection, Func<string, Task> sql)
    {
        await using var db = Open(connection);
        await db.GetService<IMigrator>().MigrateAsync("20261127054008_RaidReservations");
        await sql($"""
            INSERT INTO [Players] ([PlayerId], [UserId], [PasswordHash], [Revision], [UpdatedAt])
            VALUES ('{LegacyId:D}', N'legacy-player', N'not-a-login-hash', 17, SYSUTCDATETIME());
            INSERT INTO [Containers] ([ContainerId], [PlayerId], [OwnerItemId], [LayoutId], [StorageTag], [Level])
            VALUES ('{Guid.NewGuid():D}', '{LegacyId:D}', NULL, N'Stash_Main_03', N'Storage.Stash.Main', 3);
            """);
    }

    public static async Task<PlayerDataSnapshot> RunAsync(HttpClient a, HttpClient peer, HttpClient b,
        string connection, Action<bool, string> expect, Func<string, Task> sql)
    {
        await using (var db = Open(connection))
        {
            var legacy = await db.Players.SingleAsync(p => p.PlayerId == LegacyId);
            expect(legacy.Nickname == "legacy-player" && legacy.Revision == 17 && legacy.PasswordHash == "not-a-login-hash",
                "player migration preserves existing identity, password and inventory revision");
            expect((await db.BlackSites.SingleAsync(p => p.PlayerId == LegacyId)).StashLevel == 3
                && (await db.PlayerRuntime.SingleAsync(p => p.PlayerId == LegacyId)).RaidStatus == PlayerRaidStatus.None,
                "player migration backfills facility levels and runtime for old accounts");
        }
        using var anonymous = new HttpClient { BaseAddress = a.BaseAddress };
        expect((int)(await anonymous.GetAsync("/api/players/me")).StatusCode == 401, "anonymous player aggregate denied");
        var initial = await Get(a);
        var other = await Get(b);
        expect(initial.SchemaVersion == 1 && initial.Profile.Nickname == "demo" && initial.Profile.Level == 0
            && initial.Profile.Experience == "0" && initial.Profile.AvatarId == null && initial.Profile.DogtagImageId == null,
            "new profile has explicit defaults and optional cosmetic identifiers");
        expect(initial.Runtime.Login.IsLoggedIn && initial.Runtime.Login.ExpiresAt > initial.Runtime.Login.LastSeenAt
            && initial.Runtime.Login.LastSeenAt >= initial.Runtime.Login.LoggedInAt,
            "player login state describes the authenticated request session");
        expect(initial.Runtime.Matching.Status == PlayerMatchingStatus.Idle && initial.Runtime.Matching.Reservation == null
            && initial.Runtime.CurrentPartyId == null && initial.Runtime.Raid == new RaidRuntimeView(PlayerRaidStatus.None, null),
            "new runtime has no fabricated party, reservation or raid");
        var inventory = await a.GetFromJsonAsync<InventorySnapshot>("/api/inventory", ContractJson.Options);
        expect(Same(initial.Inventory, inventory!), "player aggregate reuses exact authoritative inventory and loadout");
        string body = await a.GetStringAsync("/api/players/me");
        expect(!body.Contains("password", StringComparison.OrdinalIgnoreCase) && !body.Contains("\"token\"", StringComparison.OrdinalIgnoreCase),
            "player response never exposes credentials or token material");
        expect((await a.GetAsync("/api/players/me")).Headers.CacheControl?.NoStore == true, "private player response disables caching");
        expect((await a.GetFromJsonAsync<PlayerDataSnapshot>($"/api/players/me?playerId={other.Profile.PlayerId:D}", ContractJson.Options))!
            .Profile.PlayerId == initial.Profile.PlayerId, "caller cannot override aggregate ownership");
        expect((int)(await a.PutAsJsonAsync("/api/players/me", new { level = 9999 })).StatusCode == 405,
            "client cannot overwrite progression through player snapshot");

        Guid pending = Guid.NewGuid(), ready = Guid.NewGuid(), claimed = Guid.NewGuid(), foreign = Guid.NewGuid();
        await sql($"""
            UPDATE [Players] SET [Nickname] = N'플레이어 테스트', [Level] = 7, [Experience] = 9007199254740993,
                [AvatarId] = N'avatar.test', [DogtagImageId] = N'dogtag.test' WHERE [PlayerId] = '{initial.Profile.PlayerId:D}';
            UPDATE [BlackSites] SET [CommandCenterLevel] = 2, [TrainingCenterLevel] = 1 WHERE [PlayerId] = '{initial.Profile.PlayerId:D}';
            INSERT INTO [CraftingJobs] ([JobId], [PlayerId], [FacilityId], [RecipeId], [Quantity], [StartedAt], [CompletesAt], [Status], [ClaimedAt]) VALUES
                ('{pending:D}', '{initial.Profile.PlayerId:D}', N'CommandCenter', N'pending', 2, SYSDATETIMEOFFSET(), DATEADD(day, 1, SYSDATETIMEOFFSET()), N'InProgress', NULL),
                ('{ready:D}', '{initial.Profile.PlayerId:D}', N'CommandCenter', N'ready', 1, DATEADD(hour, -2, SYSDATETIMEOFFSET()), DATEADD(hour, -1, SYSDATETIMEOFFSET()), N'InProgress', NULL),
                ('{claimed:D}', '{initial.Profile.PlayerId:D}', N'CommandCenter', N'claimed', 1, DATEADD(hour, -2, SYSDATETIMEOFFSET()), DATEADD(hour, -1, SYSDATETIMEOFFSET()), N'Claimed', SYSDATETIMEOFFSET()),
                ('{foreign:D}', '{other.Profile.PlayerId:D}', N'CommandCenter', N'foreign', 1, SYSDATETIMEOFFSET(), DATEADD(day, 1, SYSDATETIMEOFFSET()), N'InProgress', NULL);
            """);
        var loaded = await Get(a);
        expect(loaded.Profile.Nickname == "플레이어 테스트" && loaded.Profile.Level == 7 && loaded.Profile.Experience == "9007199254740993"
            && loaded.Profile.AvatarId == "avatar.test" && loaded.Profile.DogtagImageId == "dogtag.test",
            "profile reads durable values and preserves int64 experience beyond JavaScript precision");
        expect(loaded.BlackSite.CommandCenterLevel == 2 && loaded.BlackSite.TrainingCenterLevel == 1,
            "facility progression comes from server persistence");
        expect(loaded.Crafting.Length == 2 && loaded.Crafting.Single(j => j.JobId == pending).Status == CraftingStatus.InProgress
            && loaded.Crafting.Single(j => j.JobId == ready).Status == CraftingStatus.ReadyToClaim,
            "crafting uses server time and excludes claimed/foreign jobs");
        expect((await Get(b)).Crafting.Single().JobId == foreign && (await Get(b)).Profile.Level == 0,
            "profile, facilities and crafting stay isolated between players");
        expect(Same(inventory!, loaded.Inventory) && Same(loaded.Profile, (await Get(peer)).Profile),
            "player reads grant no crafting rewards and persist across API processes");
        await using (var db = Open(connection))
            expect((await db.CraftingJobs.SingleAsync(j => j.JobId == ready)).Status == CraftingStatus.InProgress,
                "crafting display readiness has no persistence side effects");
        await RejectSql($"UPDATE [Players] SET [Level] = -1 WHERE [PlayerId] = '{initial.Profile.PlayerId:D}'");
        await RejectSql($"UPDATE [PlayerRuntime] SET [RaidId] = '{Guid.NewGuid():D}' WHERE [PlayerId] = '{initial.Profile.PlayerId:D}'");
        var reserve = await a.PostAsJsonAsync("/api/raids/reservations", new ReserveRaidRequest(Guid.NewGuid(), "beacon"), ContractJson.Options);
        var entry = (await reserve.Content.ReadFromJsonAsync<RaidReservation>(ContractJson.Options))!;
        var matching = await Get(a);
        expect((int)reserve.StatusCode == 201 && matching.Runtime.Matching.Status == PlayerMatchingStatus.Matching
            && matching.Runtime.Matching.Reservation?.EntryId == entry.EntryId && matching.Runtime.Raid.Status == PlayerRaidStatus.None,
            "reserved matchmaking appears in aggregate without inventing active raid state");
        await a.PostAsJsonAsync($"/api/raids/{entry.RaidId}/entry/cancel", new CancelRaidRequest(Guid.NewGuid(), entry.EntryId), ContractJson.Options);
        expect((await Get(a)).Runtime.Matching.Status == PlayerMatchingStatus.Idle, "aggregate reflects confirmed matching cancellation");
        return await Get(a);

        async Task RejectSql(string command)
        {
            try { await sql(command); }
            catch (SqlException error) when (error.Number == 547) { expect(true, "SQL protects player progression/runtime invariants"); return; }
            throw new InvalidOperationException("SQL accepted an invalid player state");
        }
    }

    public static async Task VerifyRestoredAsync(HttpClient client, PlayerDataSnapshot before, Action<bool, string> expect)
    {
        var restored = await Get(client);
        expect(Same(before.Profile, restored.Profile) && Same(before.BlackSite, restored.BlackSite) && Same(before.Crafting, restored.Crafting),
            "restart/password seed preserves profile, facility progression and crafting");
        expect(restored.Runtime.Login.LoggedInAt > before.Runtime.Login.LoggedInAt,
            "relogin creates a new session without resetting player data");
    }

    private static InventoryDbContext Open(string connection) => new(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlServer(connection).Options);
    private static async Task<PlayerDataSnapshot> Get(HttpClient client)
        => (await client.GetFromJsonAsync<PlayerDataSnapshot>("/api/players/me", ContractJson.Options))!;
    private static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, ContractJson.Options) == JsonSerializer.Serialize(b, ContractJson.Options);
}
