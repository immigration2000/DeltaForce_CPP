using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaForce.Domain;

namespace DeltaForce.Application;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}

public sealed record ScopeRevision(string ScopeId, string Revision);
public sealed record InventoryCommand(Guid RequestId, OperationKind Operation, Guid ItemId,
    InventoryLocation Source, InventoryLocation Destination, ScopeRevision[] ExpectedRevisions,
    string DefinitionVersion, Guid? TargetItemId = null, int? Quantity = null);
public sealed record ItemView(Guid ItemId, string DefinitionId, int Quantity,
    double MaxDurability, double CurrentDurability, InventoryLocation Location);
public sealed record ContainerView(Guid ContainerId, Guid? OwnerItemId, string LayoutId,
    string StorageTag, int Level, bool IsAccessible, GridDefinition[] Grids);
public sealed record SlotView(string SlotTag, Guid? ItemId);
public sealed record InventorySnapshot(int SchemaVersion, string DefinitionVersion, string RulesVersion,
    string InventoryScope, string Revision, ItemView[] Items, ContainerView[] Containers, SlotView[] Slots);
public sealed record ScopeChange(string ScopeId, string BaseRevision, string NewRevision,
    ItemView[] AddedItems, ItemView[] UpdatedItems, Guid[] RemovedItemIds,
    ContainerView[] ContainerChanges, SlotView[] SlotChanges);
public sealed record InventoryDelta(Guid TransactionId, ScopeChange[] ScopeChanges);
public sealed record OperationResult(Guid RequestId, string Status, string? ErrorCode, bool Retryable,
    ScopeRevision[] BaseRevisions, ScopeRevision[] NewRevisions, InventoryDelta? Delta);
public sealed record OperationResponse(int StatusCode, OperationResult Result);
public sealed record LoginRequest(string UserId, string Password);
public sealed record LoginResponse(Guid PlayerId, string UserId, string Token, DateTimeOffset ExpiresAt);
public sealed record ApiError(string ErrorCode, bool Retryable = false);
public sealed record AuthenticatedPlayer(Guid PlayerId, string UserId);

public interface IInventoryService
{
    Task<InventorySnapshot?> GetAsync(Guid playerId, CancellationToken ct);
    Task<OperationResponse> ExecuteAsync(Guid playerId, InventoryCommand command, CancellationToken ct);
    Task<OperationResponse?> GetResultAsync(Guid playerId, Guid requestId, CancellationToken ct);
}

public interface IAccountService
{
    Task<AuthenticatedPlayer?> VerifyAsync(string userId, string password, CancellationToken ct);
}

public static class InventoryViews
{
    public const string Scope = "persistent:self";
    public static ItemView Item(InventoryItem item, DefinitionCatalog catalog)
        => new(item.ItemId, item.DefinitionId, item.Quantity, catalog.Items[item.DefinitionId].MaxDurability,
            item.CurrentDurability, item.Location.Canonical());
    public static ContainerView Container(InventoryState state, InventoryContainer container, DefinitionCatalog catalog)
        => new(container.ContainerId, container.OwnerItemId, container.LayoutId, container.StorageTag, container.Level,
            InventoryEngine.IsAccessible(state, container), catalog.Layouts[container.LayoutId].Grids);
    public static SlotView[] Slots(InventoryState state, DefinitionCatalog catalog)
        => catalog.Slots.Keys.Order(StringComparer.Ordinal).Select(tag => new SlotView(tag,
            state.Items.Values.SingleOrDefault(i => i.Location.Type == LocationType.EquipmentSlot && i.Location.SlotTag == tag)?.ItemId)).ToArray();
    public static InventorySnapshot Snapshot(InventoryState state, long revision, DefinitionCatalog catalog)
        => new(1, catalog.Version, catalog.Manifest.RulesVersion, Scope, revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            state.Items.Values.OrderBy(i => i.ItemId).Select(i => Item(i, catalog)).ToArray(),
            state.Containers.Values.OrderBy(c => c.ContainerId).Select(c => Container(state, c, catalog)).ToArray(), Slots(state, catalog));
}
