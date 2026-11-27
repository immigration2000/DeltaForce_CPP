namespace DeltaForce.Domain;

public enum LocationType { Unplaced, Grid, EquipmentSlot }
public enum OperationKind { Move, Rotate, Equip, Unequip, Swap, Merge, Split }
public sealed record Cell(int X, int Y);
public sealed record InventoryLocation(LocationType Type, Guid? ContainerId = null, int GridIndex = -1,
    Cell? CellPosition = null, bool Rotated = false, string? SlotTag = null)
{
    public static InventoryLocation Grid(Guid container, int x, int y, bool rotated = false, int grid = 0)
        => new(LocationType.Grid, container, grid, new(x, y), rotated);
    public static InventoryLocation Slot(string tag) => new(LocationType.EquipmentSlot, SlotTag: tag);
    public InventoryLocation Canonical() => Type switch
    {
        LocationType.EquipmentSlot => this with { CellPosition = new(0, 0) },
        LocationType.Grid => this with { SlotTag = null },
        _ => this
    };

    public bool IsValid() => Type switch
    {
        LocationType.Grid => ContainerId is { } id && id != Guid.Empty && GridIndex >= 0
            && CellPosition is { X: >= 0, Y: >= 0 } && string.IsNullOrEmpty(SlotTag),
        LocationType.EquipmentSlot => ContainerId == null && GridIndex == -1 && !Rotated
            && (CellPosition == null || CellPosition == new Cell(0, 0)) && !string.IsNullOrWhiteSpace(SlotTag),
        _ => false
    };
}

public sealed record ItemDefinition(string Id, string DisplayName, string CategoryTag, string[] ItemTags,
    int Width, int Height, int MaxStack, int ItemLevel, double MaxDurability);
public sealed record GridDefinition(int Width, int Height);
public sealed record LayoutDefinition(string Id, GridDefinition[] Grids);
public sealed record SlotRule(string Tag, string[] AllowedCategories, string[] ExcludedCategories);
public sealed record StorageRule(string Tag, bool AllowAnyItem, string[] AllowedTags);
public sealed record DefinitionManifest(int SchemaVersion, string RulesVersion, string Source,
    ItemDefinition[] Items, LayoutDefinition[] Layouts, SlotRule[] Slots, StorageRule[] StorageRules);

public sealed class DefinitionCatalog
{
    public string Version { get; }
    public DefinitionManifest Manifest { get; }
    public IReadOnlyDictionary<string, ItemDefinition> Items { get; }
    public IReadOnlyDictionary<string, LayoutDefinition> Layouts { get; }
    public IReadOnlyDictionary<string, SlotRule> Slots { get; }
    public IReadOnlyDictionary<string, StorageRule> StorageRules { get; }
    public DefinitionCatalog(DefinitionManifest manifest, string version)
    {
        if (manifest.SchemaVersion != 1 || manifest.Items.Any(i => i.Width <= 0 || i.Height <= 0
            || i.MaxStack <= 0 || !double.IsFinite(i.MaxDurability) || i.MaxDurability < 0)
            || manifest.Layouts.Any(l => l.Grids.Length == 0 || l.Grids.Any(g => g.Width <= 0 || g.Height <= 0)))
            throw new ArgumentException("Invalid definition manifest");
        Manifest = manifest;
        Version = version;
        Items = manifest.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        Layouts = manifest.Layouts.ToDictionary(l => l.Id, StringComparer.Ordinal);
        Slots = manifest.Slots.ToDictionary(s => s.Tag, StringComparer.Ordinal);
        StorageRules = manifest.StorageRules.ToDictionary(s => s.Tag, StringComparer.Ordinal);
    }
    public static bool MatchesTag(string tag, string parent)
        => tag == parent || tag.StartsWith(parent + ".", StringComparison.Ordinal);
}

public sealed record InventoryItem(Guid ItemId, string DefinitionId, int Quantity,
    double CurrentDurability, InventoryLocation Location);
public sealed record InventoryContainer(Guid ContainerId, Guid? OwnerItemId, string LayoutId,
    string StorageTag, int Level = 0);
public sealed record InventoryState(IReadOnlyDictionary<Guid, InventoryItem> Items,
    IReadOnlyDictionary<Guid, InventoryContainer> Containers);
public sealed record InventoryIntent(OperationKind Operation, Guid ItemId, InventoryLocation Source,
    InventoryLocation Destination, Guid? TargetItemId = null, int? Quantity = null);
public sealed record InventoryMutation(InventoryState State, InventoryItem[] UpdatedItems,
    InventoryItem[] AddedItems, Guid[] RemovedItemIds);
public sealed class InventoryRuleException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static class PlacementRules
{
    public static bool Fits(GridDefinition grid, ItemDefinition item, InventoryLocation location)
    {
        if (location.CellPosition is not { } cell) return false;
        int width = location.Rotated ? item.Height : item.Width;
        int height = location.Rotated ? item.Width : item.Height;
        return cell.X >= 0 && cell.Y >= 0 && cell.X <= grid.Width - width && cell.Y <= grid.Height - height;
    }

    // Stable GridIndex -> Y -> X order; preserves the requested rotation.
    public static InventoryLocation? FindFirstFit(InventoryState state, DefinitionCatalog catalog,
        Guid containerId, string definitionId, bool rotated)
    {
        var container = state.Containers[containerId];
        var definition = catalog.Items[definitionId];
        var grids = catalog.Layouts[container.LayoutId].Grids;
        for (int g = 0; g < grids.Length; g++)
            for (int y = 0; y < grids[g].Height; y++)
                for (int x = 0; x < grids[g].Width; x++)
                {
                    var location = InventoryLocation.Grid(containerId, x, y, rotated, g);
                    if (Fits(grids[g], definition, location) && !state.Items.Values.Any(i =>
                        i.Location.Type == LocationType.Grid && i.Location.ContainerId == containerId
                        && i.Location.GridIndex == g && Overlaps(definition, location, catalog.Items[i.DefinitionId], i.Location)))
                        return location;
                }
        return null;
    }

    public static bool Overlaps(ItemDefinition a, InventoryLocation x, ItemDefinition b, InventoryLocation y)
    {
        var p = x.CellPosition!;
        var q = y.CellPosition!;
        return (long)p.X < (long)q.X + (y.Rotated ? b.Height : b.Width)
            && (long)q.X < (long)p.X + (x.Rotated ? a.Height : a.Width)
            && (long)p.Y < (long)q.Y + (y.Rotated ? b.Width : b.Height)
            && (long)q.Y < (long)p.Y + (x.Rotated ? a.Width : a.Height);
    }
}

public sealed class InventoryEngine(DefinitionCatalog catalog)
{
    private static void Require(bool condition, string code)
    { if (!condition) throw new InventoryRuleException(code); }

    public static bool IsAccessible(InventoryState state, InventoryContainer container)
        => container.OwnerItemId == null || (state.Items.TryGetValue(container.OwnerItemId.Value, out var owner)
            && owner.Location.Type == LocationType.EquipmentSlot);

    private static void CheckAccess(InventoryState state, InventoryLocation location)
    {
        if (location.Type != LocationType.Grid) return;
        Require(state.Containers.TryGetValue(location.ContainerId!.Value, out var container), "ContainerNotFound");
        Require(IsAccessible(state, container!), "ContainerAccessDenied");
    }

    public InventoryMutation Execute(InventoryState state, InventoryIntent intent, Func<Guid>? newId = null)
    {
        Require(intent.Source.IsValid() && intent.Destination.IsValid(), "InvalidLocation");
        Require(state.Items.TryGetValue(intent.ItemId, out var item), "ItemNotFound");
        Require(item!.Location.Canonical() == intent.Source.Canonical(), "SourceMismatch");
        CheckAccess(state, intent.Source);
        var items = state.Items.ToDictionary(p => p.Key, p => p.Value);
        InventoryItem? target = null;
        bool paired = intent.Operation is OperationKind.Swap or OperationKind.Merge;
        Require(paired == intent.TargetItemId.HasValue, "InvalidRequest");
        if (paired)
        {
            Require(intent.TargetItemId != intent.ItemId && items.TryGetValue(intent.TargetItemId!.Value, out target), "ItemNotFound");
            Require(target!.Location.Canonical() == intent.Destination.Canonical(), "SourceMismatch");
            CheckAccess(state, target.Location);
        }
        Require(intent.Quantity == null || intent.Operation is OperationKind.Merge or OperationKind.Split, "InvalidRequest");
        var destination = intent.Destination.Canonical();
        switch (intent.Operation)
        {
            case OperationKind.Equip:
                Require(destination.Type == LocationType.EquipmentSlot, "InvalidLocation");
                items[item.ItemId] = item with { Location = destination };
                break;
            case OperationKind.Unequip:
                Require(item.Location.Type == LocationType.EquipmentSlot && destination.Type == LocationType.Grid, "InvalidLocation");
                items[item.ItemId] = item with { Location = destination };
                break;
            case OperationKind.Rotate:
                Require(item.Location.Type == LocationType.Grid && destination.Type == LocationType.Grid
                    && item.Location.ContainerId == destination.ContainerId && item.Location.GridIndex == destination.GridIndex
                    && item.Location.CellPosition == destination.CellPosition && item.Location.Rotated != destination.Rotated, "InvalidLocation");
                items[item.ItemId] = item with { Location = destination };
                break;
            case OperationKind.Move:
                items[item.ItemId] = item with { Location = destination };
                break;
            case OperationKind.Swap:
                items[item.ItemId] = item with { Location = destination };
                items[target!.ItemId] = target with { Location = intent.Source.Canonical() };
                break;
            case OperationKind.Merge:
                Require(item.DefinitionId == target!.DefinitionId && item.CurrentDurability == target.CurrentDurability, "StackMismatch");
                int count = intent.Quantity ?? item.Quantity;
                Require(count > 0 && count <= item.Quantity && count <= catalog.Items[target.DefinitionId].MaxStack - target.Quantity, "InvalidQuantity");
                Require(!state.Containers.Values.Any(c => c.OwnerItemId == item.ItemId), "StackMismatch");
                items[target.ItemId] = target with { Quantity = target.Quantity + count };
                if (count == item.Quantity) items.Remove(item.ItemId);
                else items[item.ItemId] = item with { Quantity = item.Quantity - count };
                break;
            case OperationKind.Split:
                Require(intent.Quantity is > 0 && intent.Quantity < item.Quantity, "InvalidQuantity");
                Require(!state.Containers.Values.Any(c => c.OwnerItemId == item.ItemId), "StackMismatch");
                items[item.ItemId] = item with { Quantity = item.Quantity - intent.Quantity!.Value };
                Guid id = (newId ?? Guid.NewGuid)();
                Require(id != Guid.Empty && !items.ContainsKey(id), "DuplicateItemId");
                items.Add(id, item with { ItemId = id, Quantity = intent.Quantity.Value, Location = destination });
                break;
            default: throw new InventoryRuleException("InvalidRequest");
        }
        var next = new InventoryState(items, state.Containers);
        Validate(next);
        CheckAccess(next, destination);
        return new(next, items.Values.Where(i => state.Items.TryGetValue(i.ItemId, out var old) && i != old).ToArray(),
            items.Values.Where(i => !state.Items.ContainsKey(i.ItemId)).ToArray(), state.Items.Keys.Except(items.Keys).ToArray());
    }

    public void Validate(InventoryState state)
    {
        foreach (var container in state.Containers.Values)
        {
            Require(catalog.Layouts.ContainsKey(container.LayoutId) && catalog.StorageRules.ContainsKey(container.StorageTag), "InvalidContainer");
            var seen = new HashSet<Guid>();
            var cursor = container;
            while (cursor.OwnerItemId is { } ownerId)
            {
                Require(seen.Add(cursor.ContainerId), "CycleDetected");
                Require(state.Items.TryGetValue(ownerId, out var owner), "InvalidContainer");
                if (owner!.Location.Type != LocationType.Grid) break;
                Require(state.Containers.TryGetValue(owner.Location.ContainerId!.Value, out cursor!), "ContainerNotFound");
            }
        }
        var occupied = new HashSet<(Guid Container, int Grid, int X, int Y)>();
        var slots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in state.Items.Values)
        {
            Require(item.ItemId != Guid.Empty && catalog.Items.TryGetValue(item.DefinitionId, out _), "InvalidItemDefinition");
            var definition = catalog.Items[item.DefinitionId];
            Require(item.Quantity > 0 && item.Quantity <= definition.MaxStack, "InvalidQuantity");
            Require(double.IsFinite(item.CurrentDurability) && item.CurrentDurability >= 0 && item.CurrentDurability <= definition.MaxDurability, "InvalidDurability");
            var location = item.Location;
            Require(location.IsValid(), "InvalidLocation");
            if (location.Type == LocationType.EquipmentSlot)
            {
                Require(item.Quantity == 1 && catalog.Slots.TryGetValue(location.SlotTag!, out _), "InvalidEquipmentSlot");
                var rule = catalog.Slots[location.SlotTag!];
                Require(rule.AllowedCategories.Any(t => DefinitionCatalog.MatchesTag(definition.CategoryTag, t))
                    && !rule.ExcludedCategories.Any(t => DefinitionCatalog.MatchesTag(definition.CategoryTag, t)), "CategoryMismatch");
                // Secure cases require explicit per-definition slot rules, which are not yet authored.
                Require(location.SlotTag is not ("Slot.SafeBox" or "Slot.KeyHolder"), "EquipmentSlotRuleMissing");
                Require(slots.Add(location.SlotTag!), "Collision");
                continue;
            }
            Require(state.Containers.TryGetValue(location.ContainerId!.Value, out _), "ContainerNotFound");
            var container = state.Containers[location.ContainerId.Value];
            var grids = catalog.Layouts[container.LayoutId].Grids;
            Require(location.GridIndex < grids.Length && PlacementRules.Fits(grids[location.GridIndex], definition, location), "OutOfBounds");
            var storage = catalog.StorageRules[container.StorageTag];
            Require(storage.AllowAnyItem || storage.AllowedTags.Any(tag => DefinitionCatalog.MatchesTag(definition.CategoryTag, tag)
                || definition.ItemTags.Any(t => DefinitionCatalog.MatchesTag(t, tag))), "CategoryMismatch");
            Require(container.Level == 0 || definition.ItemLevel <= container.Level, "ItemLevelTooHigh");
            int width = location.Rotated ? definition.Height : definition.Width;
            int height = location.Rotated ? definition.Width : definition.Height;
            for (int y = location.CellPosition!.Y; y < location.CellPosition.Y + height; y++)
                for (int x = location.CellPosition.X; x < location.CellPosition.X + width; x++)
                    Require(occupied.Add((container.ContainerId, location.GridIndex, x, y)), "Collision");
        }
    }
}
