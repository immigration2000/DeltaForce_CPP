namespace DeltaForce.Application;

public enum CraftingStatus { InProgress, ReadyToClaim, Claimed, Cancelled }
public enum PlayerRaidStatus { None, Preparing, Joining, InRaid, Finished }
public enum PlayerMatchingStatus { Idle, Matching }

public sealed record PlayerProfileView(Guid PlayerId, string Nickname, int Level, string Experience,
    string? AvatarId, string? DogtagImageId);
public sealed record BlackSiteView(int StashLevel, int CommandCenterLevel, int TrainingCenterLevel,
    int FiringRangeLevel, int DivingCenterLevel, int CollectionRoomLevel, int DecontaminationRoomLevel);
public sealed record CraftingJobView(Guid JobId, string FacilityId, string RecipeId, int Quantity,
    DateTimeOffset StartedAt, DateTimeOffset CompletesAt, CraftingStatus Status);
// Describes the token used for this request, not the player's online/offline presence.
public sealed record LoginSessionView(bool IsLoggedIn, DateTimeOffset LoggedInAt,
    DateTimeOffset ExpiresAt, DateTimeOffset LastSeenAt);
public sealed record MatchingRuntimeView(PlayerMatchingStatus Status, RaidReservation? Reservation);
public sealed record RaidRuntimeView(PlayerRaidStatus Status, Guid? RaidId);
public sealed record PlayerRuntimeView(LoginSessionView Login, Guid? CurrentPartyId,
    MatchingRuntimeView Matching, RaidRuntimeView Raid);
public sealed record PlayerDataSnapshot(int SchemaVersion, PlayerProfileView Profile, BlackSiteView BlackSite,
    CraftingJobView[] Crafting, InventorySnapshot Inventory, PlayerRuntimeView Runtime);

public interface IPlayerDataService
{
    Task<PlayerDataSnapshot?> GetAsync(Guid playerId, LoginSessionView session, CancellationToken ct);
}
