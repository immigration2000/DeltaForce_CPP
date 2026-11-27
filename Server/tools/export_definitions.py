"""Normalize committed UE JSON exports. Run explicitly; never rewrites UE assets."""
import argparse
import hashlib
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("source", type=Path, help="Existing Grid-based-Inventory-System checkout")
args = parser.parse_args()
source = args.source.resolve()
root = Path(__file__).resolve().parents[2]
def read(name):
    return json.loads((source / name).read_text(encoding="utf-8-sig"))
def tag(value):
    return value.get("TagName", "")
items = read("DT_ItemDefinition.json")
layouts = read("DT_ContainerLayout.json")
storage = read("DT_StorageRules.json")
slot_categories = {
    "Helmet": "Equipment.Helmet", "Armor": "Equipment.Armor", "Backpack": "Container.Backpack",
    "ChestRig": "Container.ChestRig", "Sidearm": "Weapon.Pistol", "Melee": "Weapon.Melee",
    "Primary01": "Weapon", "Primary02": "Weapon",
    "SafeBox": "Container.SecureCase", "KeyHolder": "Container.SecureCase",
}
manifest = {
    "schemaVersion": 1,
    "rulesVersion": "inventory-v1",
    "source": "Grid-based-Inventory-System UE exports; durability is unmodelled (0), not an original game value",
    "items": [{"id": i["Name"], "displayName": i["DisplayName"], "categoryTag": tag(i["CategoryTag"]),
               "itemTags": [tag(t) for t in i["ItemTags"]["GameplayTags"]],
               "width": i["ItemSize"]["X"], "height": i["ItemSize"]["Y"],
               "maxStack": i["MaxStack"], "itemLevel": i["ItemLevel"], "maxDurability": 0}
              for i in items],
    "layouts": [{"id": l["Name"], "grids": [{"width": g["GridSize"]["X"], "height": g["GridSize"]["Y"]}
                                                 for g in l["Grids"]]} for l in layouts],
    "slots": [{"tag": "Slot." + name, "allowedCategories": ["Item.Category." + category],
               "excludedCategories": ["Item.Category.Weapon.Pistol", "Item.Category.Weapon.Melee"]
               if name.startswith("Primary") else []} for name, category in slot_categories.items()],
    "storageRules": [{"tag": r["Name"], "allowAnyItem": r["bAllowAnyItem"],
                      "allowedTags": [tag(t) for t in r["AllowedItemTags"]["GameplayTags"]]} for r in storage],
}
for name in ("Storage.Backpack", "Storage.ChestRig"):
    if not any(r["tag"] == name for r in manifest["storageRules"]):
        manifest["storageRules"].append({"tag": name, "allowAnyItem": True, "allowedTags": []})
output = root / "Contracts/Definitions/inventory.v1.json"
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
print(f"{len(items)} item definitions, {len(layouts)} layouts; sha256:{hashlib.sha256(output.read_bytes()).hexdigest()}")
