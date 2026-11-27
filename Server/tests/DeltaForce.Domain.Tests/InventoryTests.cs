using System.Text.Json;
using DeltaForce.Domain;
using Xunit;

namespace DeltaForce.Domain.Tests;

public sealed class InventoryTests
{
    private static readonly Guid Root = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid First = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly DefinitionCatalog Catalog = new(new(1, "test", "test",
        [new("small", "Small", "Item.Material", [], 1, 1, 1, 1, 0),
         new("large", "Large", "Item.Material", [], 2, 2, 1, 1, 0),
         new("helmet", "Helmet", "Item.Helmet.Heavy", [], 2, 2, 1, 1, 0),
         new("ammo", "Ammo", "Item.Ammo", [], 1, 1, 60, 1, 0),
         new("bag", "Bag", "Item.Bag", [], 1, 2, 1, 1, 0)],
        [new("root", [new(4, 4)]), new("bag", [new(2, 3)])],
        [new("Slot.Helmet", ["Item.Helmet"], []), new("Slot.Backpack", ["Item.Bag"], [])],
        [new("Storage.Main", true, []), new("Storage.Ammo", false, ["Item.Ammo"])]), "test");
    private static InventoryState State(params InventoryItem[] items)
        => new(items.ToDictionary(i => i.ItemId), new Dictionary<Guid, InventoryContainer>
            { [Root] = new(Root, null, "root", "Storage.Main") });
    private static InventoryItem Item(Guid id, string definition, int x, int y, int quantity = 1)
        => new(id, definition, quantity, 0, InventoryLocation.Grid(Root, x, y));
    private static InventoryIntent Intent(InventoryItem item, InventoryLocation target, OperationKind operation = OperationKind.Move,
        Guid? targetId = null, int? quantity = null) => new(operation, item.ItemId, item.Location, target, targetId, quantity);

    [Fact]
    public void SharedPlacementVectors()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "placement.v1.json")));
        foreach (var v in file.RootElement.EnumerateArray())
        {
            var grid = new GridDefinition(v.GetProperty("gridWidth").GetInt32(), v.GetProperty("gridHeight").GetInt32());
            var definition = new ItemDefinition("vector", "", "", [], v.GetProperty("itemWidth").GetInt32(), v.GetProperty("itemHeight").GetInt32(), 1, 1, 0);
            var location = InventoryLocation.Grid(Root, v.GetProperty("x").GetInt32(), v.GetProperty("y").GetInt32(), v.GetProperty("rotated").GetBoolean());
            Assert.Equal(v.GetProperty("fits").GetBoolean(), PlacementRules.Fits(grid, definition, location));
        }
    }
    [Theory]
    [InlineData(2, 0, false)]
    [InlineData(1, 1, true)]
    public void HalfOpenOccupancy(int x, int y, bool collision)
    {
        var large = Catalog.Items["large"];
        Assert.Equal(collision, PlacementRules.Overlaps(large, InventoryLocation.Grid(Root, 0, 0),
            Catalog.Items["small"], InventoryLocation.Grid(Root, x, y)));
    }
    [Fact]
    public void FirstFitUsesGridThenYThenX()
    {
        var state = State(Item(First, "small", 0, 0));
        Assert.Equal(InventoryLocation.Grid(Root, 1, 0), PlacementRules.FindFirstFit(state, Catalog, Root, "small", false));
    }
    [Fact]
    public void FailedSwapDoesNotChangeEitherItem()
    {
        var a = Item(First, "small", 0, 0); var b = Item(Second, "large", 1, 0);
        var state = State(a, b, Item(Guid.NewGuid(), "small", 0, 1));
        var error = Assert.Throws<InventoryRuleException>(() => new InventoryEngine(Catalog).Execute(state, Intent(a, b.Location, OperationKind.Swap, b.ItemId)));
        Assert.Equal("Collision", error.Code);
        Assert.Equal(a, state.Items[First]); Assert.Equal(b, state.Items[Second]);
    }
    [Fact]
    public void EquipmentUsesHierarchicalCategoriesAndCanonicalLocation()
    {
        var item = Item(First, "helmet", 0, 0);
        var result = new InventoryEngine(Catalog).Execute(State(item), Intent(item, InventoryLocation.Slot("Slot.Helmet"), OperationKind.Equip));
        Assert.Equal(InventoryLocation.Slot("Slot.Helmet").Canonical(), result.State.Items[First].Location);
        Assert.Equal("CategoryMismatch", Assert.Throws<InventoryRuleException>(() => new InventoryEngine(Catalog)
            .Execute(State(item), Intent(item, InventoryLocation.Slot("Slot.Backpack"), OperationKind.Equip))).Code);
    }
    [Fact]
    public void ContainerCannotBeInsertedIntoItsOwnDescendants()
    {
        var bag = Item(First, "bag", 0, 0);
        var state = State(bag);
        state = state with { Containers = new Dictionary<Guid, InventoryContainer>(state.Containers)
            { [First] = new(First, First, "bag", "Storage.Main") } };
        Assert.Equal("CycleDetected", Assert.Throws<InventoryRuleException>(() => new InventoryEngine(Catalog)
            .Execute(state, Intent(bag, InventoryLocation.Grid(First, 0, 0)))).Code);
    }
    [Fact]
    public void SplitAndMergeConserveQuantityAndRetireOnlyMergedId()
    {
        var item = Item(First, "ammo", 0, 0, 30);
        var split = new InventoryEngine(Catalog).Execute(State(item), Intent(item, InventoryLocation.Grid(Root, 1, 0), OperationKind.Split, quantity: 10), () => Second);
        Assert.Equal(30, split.State.Items.Values.Sum(i => i.Quantity));
        Assert.Equal(20, split.State.Items[First].Quantity);
        var merge = new InventoryEngine(Catalog).Execute(split.State, Intent(split.State.Items[Second], split.State.Items[First].Location, OperationKind.Merge, First));
        Assert.Equal(30, merge.State.Items[First].Quantity);
        Assert.Equal([Second], merge.RemovedItemIds);
    }
    [Fact]
    public void InaccessibleBagContentsCannotBeChanged()
    {
        var bag = Item(First, "bag", 0, 0);
        var ammo = new InventoryItem(Second, "ammo", 1, 0, InventoryLocation.Grid(First, 0, 0));
        var state = State(bag, ammo);
        state = state with { Containers = new Dictionary<Guid, InventoryContainer>(state.Containers)
            { [First] = new(First, First, "bag", "Storage.Main") } };
        Assert.Equal("ContainerAccessDenied", Assert.Throws<InventoryRuleException>(() => new InventoryEngine(Catalog)
            .Execute(state, Intent(ammo, InventoryLocation.Grid(Root, 2, 2)))).Code);
    }
    [Fact]
    public void ContainerRestrictionCannotBeBypassedWithMove()
    {
        var helmet = Item(First, "helmet", 0, 0);
        var state = State(helmet);
        var ammoBox = Guid.NewGuid();
        state = state with { Containers = new Dictionary<Guid, InventoryContainer>(state.Containers)
            { [ammoBox] = new(ammoBox, null, "root", "Storage.Ammo") } };
        Assert.Equal("CategoryMismatch", Assert.Throws<InventoryRuleException>(() => new InventoryEngine(Catalog)
            .Execute(state, Intent(helmet, InventoryLocation.Grid(ammoBox, 0, 0)))).Code);
    }
}
