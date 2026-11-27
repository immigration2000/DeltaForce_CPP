"""Build the committed OpenAPI and its small local HTTP regression collection."""
import json
from pathlib import Path

root = Path(__file__).resolve().parents[2]
def ref(name):
    return {"$ref": "#/components/schemas/" + name}
def array(value):
    return {"type": "array", "items": value}
def obj(properties, required=None):
    return {"type": "object", "additionalProperties": False, "properties": properties,
            "required": list(properties) if required is None else required}
string = {"type": "string"}
integer = {"type": "integer", "format": "int32"}
boolean = {"type": "boolean"}
guid = {"type": "string", "format": "uuid"}
nullable_guid = {**guid, "nullable": True}
revision = {"type": "string", "pattern": "^[1-9][0-9]*$", "description": "int64 encoded as a decimal string"}
location_properties = {"type": {"type": "string", "enum": ["Grid", "EquipmentSlot"]},
    "containerId": nullable_guid, "gridIndex": {**integer, "minimum": -1}, "cellPosition": ref("Cell"),
    "rotated": boolean, "slotTag": {**string, "nullable": True}}
schemas = {
    "Cell": obj({"x": integer, "y": integer}),
    "Location": {**obj(location_properties, ["type"]), "description": "Grid requires containerId, gridIndex >= 0, cellPosition >= 0, and an empty slotTag. EquipmentSlot requires slotTag, null containerId, gridIndex=-1, cellPosition omitted or (0,0), and rotated=false. Grid coordinates have a top-left origin."},
    "ScopeRevision": obj({"scopeId": {"type": "string", "enum": ["persistent:self"]}, "revision": revision}),
    "LoginRequest": obj({"userId": {**string, "minLength": 1, "maxLength": 64}, "password": {**string, "minLength": 1, "maxLength": 128, "format": "password"}}),
    "LoginResponse": obj({"playerId": guid, "userId": string, "token": {**string, "minLength": 64, "maxLength": 64}, "expiresAt": {"type": "string", "format": "date-time"}}),
    "ApiError": obj({"errorCode": string, "retryable": boolean}),
    "InventoryCommand": obj({"requestId": guid, "operation": {"type": "string", "enum": ["Move", "Rotate", "Equip", "Unequip", "Swap", "Merge", "Split"]},
        "itemId": guid, "source": ref("Location"), "destination": ref("Location"),
        "expectedRevisions": {**array(ref("ScopeRevision")), "minItems": 1, "maxItems": 1},
        "definitionVersion": string, "targetItemId": nullable_guid, "quantity": {**integer, "nullable": True, "minimum": 1}},
        ["requestId", "operation", "itemId", "source", "destination", "expectedRevisions", "definitionVersion"]),
    "Item": obj({"itemId": guid, "definitionId": string, "quantity": {**integer, "minimum": 1},
        "maxDurability": {"type": "number", "format": "double", "minimum": 0}, "currentDurability": {"type": "number", "format": "double", "minimum": 0}, "location": ref("Location")}),
    "Grid": obj({"width": {**integer, "minimum": 1}, "height": {**integer, "minimum": 1}}),
    "Container": obj({"containerId": guid, "ownerItemId": nullable_guid, "layoutId": string, "storageTag": string,
        "level": integer, "isAccessible": boolean, "grids": array(ref("Grid"))}),
    "Slot": obj({"slotTag": string, "itemId": nullable_guid}),
    "Snapshot": obj({"schemaVersion": integer, "definitionVersion": string, "rulesVersion": string,
        "inventoryScope": string, "revision": revision, "items": array(ref("Item")), "containers": array(ref("Container")), "slots": array(ref("Slot"))}),
    "ScopeChange": obj({"scopeId": string, "baseRevision": revision, "newRevision": revision, "addedItems": array(ref("Item")),
        "updatedItems": array(ref("Item")), "removedItemIds": array(guid), "containerChanges": array(ref("Container")), "slotChanges": array(ref("Slot"))}),
    "Delta": obj({"transactionId": guid, "scopeChanges": array(ref("ScopeChange"))}),
    "OperationResult": obj({"requestId": guid, "status": {"type": "string", "enum": ["Applied", "Rejected"]},
        "errorCode": {**string, "nullable": True}, "retryable": boolean, "baseRevisions": array(ref("ScopeRevision")),
        "newRevisions": array(ref("ScopeRevision")), "delta": {"allOf": [ref("Delta")], "nullable": True}}),
}
manifest_schema = obj({"schemaVersion": integer, "rulesVersion": string, "source": string,
    "items": array(obj({"id": string, "displayName": string, "categoryTag": string, "itemTags": array(string),
        "width": integer, "height": integer, "maxStack": integer, "itemLevel": integer, "maxDurability": {"type": "number"}})),
    "layouts": array(obj({"id": string, "grids": array(ref("Grid"))})),
    "slots": array(obj({"tag": string, "allowedCategories": array(string), "excludedCategories": array(string)})),
    "storageRules": array(obj({"tag": string, "allowAnyItem": boolean, "allowedTags": array(string)}))})
schemas["Definitions"] = obj({"definitionVersion": string, "manifest": manifest_schema})
def response(schema, description):
    return {"description": description, "content": {"application/json": {"schema": schema}}}
errors = {"400": response(ref("ApiError"), "Malformed request; a transport-level 400 may have no JSON body"),
          "401": response(ref("ApiError"), "Invalid/expired session"),
          "503": response(ref("ApiError"), "Dependency unavailable; check the same requestId before retrying")}
paths = {
    "/health/live": {"get": {"operationId": "liveness", "security": [], "responses": {"200": response(obj({"status": string}), "Process is alive")}}},
    "/health/ready": {"get": {"operationId": "readiness", "security": [], "responses": {"200": response(obj({"status": string, "database": string}), "MSSQL query succeeded"), "503": errors["503"]}}},
    "/api/auth/login": {"post": {"operationId": "login", "security": [], "requestBody": {"required": True, "content": {"application/json": {"schema": ref("LoginRequest")}}},
        "responses": {"200": response(ref("LoginResponse"), "New process-local session, 8-hour maximum lifetime"), "400": errors["400"], "401": errors["401"], "429": {"description": "20 attempts/minute/IP"}, "503": errors["503"]}}},
    "/api/auth/logout": {"post": {"operationId": "logout", "description": "Invalidate only this token. Inventory changes are already durable; no snapshot is uploaded.",
        "responses": {"204": {"description": "Token revoked"}, "401": errors["401"]}}},
    "/api/definitions": {"get": {"operationId": "definitions", "responses": {"200": response(ref("Definitions"), "Server-authoritative immutable manifest and SHA-256 version"), "401": errors["401"]}}},
    "/api/inventory": {"get": {"operationId": "inventory", "responses": {"200": response(ref("Snapshot"), "Consistent own-player snapshot"), "401": errors["401"], "404": response(ref("ApiError"), "Player not found"), "503": errors["503"]}}},
    "/api/inventory/operations": {"post": {"operationId": "executeInventoryOperation",
        "description": "Exactly one durable result per player+requestId. Same canonical payload returns the stored result BEFORE revision validation. A different payload is 409. Successful state changes, revision and result commit together. Records have no TTL in M1. Swap exchanges full locations. Merge uses targetItemId and optional quantity (default all). Split requires quantity and returns a server-generated GUID. updatedItems are full upserts. Apply a delta only at its baseRevision; otherwise reload the snapshot.",
        "requestBody": {"required": True, "content": {"application/json": {"schema": ref("InventoryCommand")}}},
        "responses": {"200": response(ref("OperationResult"), "Applied, or exact replay"),
            **{str(code): response(ref("OperationResult"), desc) for code, desc in [(400, "Invalid intent/location"), (403, "Container inaccessible"), (404, "Item/container not found in this account"), (409, "Revision/source/definition/payload mismatch"), (422, "Collision, category, boundary, stack or containment rule rejection")]},
            "401": errors["401"], "503": errors["503"]}}},
    "/api/inventory/operations/{requestId}": {"get": {"operationId": "inventoryOperationResult",
        "parameters": [{"name": "requestId", "in": "path", "required": True, "schema": guid}],
        "description": "Returns the stored result with its original HTTP status. Not recorded does not prove an in-flight request failed.",
        "responses": {"200": response(ref("OperationResult"), "Stored success"),
            **{str(code): response({"oneOf": [ref("OperationResult"), ref("ApiError")]}, "Stored rejection or lookup error") for code in (400, 403, 404, 409, 422)},
            "401": errors["401"], "503": errors["503"]}}},
}
schemas["ReserveRaidRequest"] = obj({"requestId": guid, "mapId": {**string, "minLength": 1, "maxLength": 64}})
schemas["CancelRaidRequest"] = obj({"requestId": guid, "entryId": guid})
schemas["RaidReservation"] = obj({"raidId": guid, "entryId": guid, "mapId": string,
    "status": {"type": "string", "enum": ["Reserved", "Cancelled"]}, "createdAt": {"type": "string", "format": "date-time"}})
raid_responses = {"200": response(ref("RaidReservation"), "Current reservation or stored result"),
    "400": errors["400"], "401": errors["401"], "404": response(ref("ApiError"), "Entry/result not found for this player"),
    "409": response(ref("ApiError"), "AlreadyMatching, RequestPayloadMismatch or EntryNotCancelable"), "503": errors["503"]}
paths["/api/raids/reservations"] = {"post": {"operationId": "reserveRaid",
    "description": "Create one durable Reserved entry per player. This is matchmaking acceptance, not a ready dedicated server. No inventory lock or revision change occurs until the future entry commit stage. Reuse the same requestId after timeout. Replays return the original historical result; query current-entry for current state.",
    "requestBody": {"required": True, "content": {"application/json": {"schema": ref("ReserveRaidRequest")}}},
    "responses": {**{k:v for k,v in raid_responses.items() if k != "200"}, "201": response(ref("RaidReservation"), "Reserved or exact replay")}}}
paths["/api/raids/current-entry"] = {"get": {"operationId": "currentRaidEntry", "responses": raid_responses}}
paths["/api/raids/{raidId}/entry"] = {"get": {"operationId": "raidEntry",
    "parameters": [{"name": "raidId", "in": "path", "required": True, "schema": guid}], "responses": raid_responses}}
paths["/api/raids/{raidId}/entry/cancel"] = {"post": {"operationId": "cancelRaidEntry",
    "description": "Cancel only the authenticated player's specified Reserved entry. Same-key retries replay; cancelling an already Cancelled entry also succeeds. Does not touch another/newer reservation.",
    "parameters": [{"name": "raidId", "in": "path", "required": True, "schema": guid}],
    "requestBody": {"required": True, "content": {"application/json": {"schema": ref("CancelRaidRequest")}}}, "responses": raid_responses}}
paths["/api/raids/requests/{requestId}"] = {"get": {"operationId": "raidRequestResult",
    "description": "Replay the original response/status for this player+requestId. A 404 RequestNotRecorded does not prove an in-flight request failed.",
    "parameters": [{"name": "requestId", "in": "path", "required": True, "schema": guid}],
    "responses": {**raid_responses, "201": response(ref("RaidReservation"), "Stored reservation result")}}}
date_time = {"type": "string", "format": "date-time"}
schemas["PlayerProfile"] = obj({"playerId": guid, "nickname": {**string, "maxLength": 64},
    "level": {**integer, "minimum": 0}, "experience": {"type": "string", "pattern": "^(0|[1-9][0-9]*)$", "description": "Nonnegative int64, decimal string"},
    "avatarId": {**string, "nullable": True, "maxLength": 128}, "dogtagImageId": {**string, "nullable": True, "maxLength": 128}})
schemas["BlackSite"] = obj({name: {**integer, "minimum": 0} for name in
    ("stashLevel", "commandCenterLevel", "trainingCenterLevel", "firingRangeLevel", "divingCenterLevel", "collectionRoomLevel", "decontaminationRoomLevel")})
schemas["CraftingJob"] = obj({"jobId": guid, "facilityId": string, "recipeId": string,
    "quantity": {**integer, "minimum": 1}, "startedAt": date_time, "completesAt": date_time,
    "status": {"type": "string", "enum": ["InProgress", "ReadyToClaim", "Claimed", "Cancelled"]}})
schemas["LoginSession"] = obj({"isLoggedIn": boolean, "loggedInAt": date_time, "expiresAt": date_time, "lastSeenAt": date_time})
schemas["MatchingRuntime"] = obj({"status": {"type": "string", "enum": ["Idle", "Matching"]},
    "reservation": {"allOf": [ref("RaidReservation")], "nullable": True}})
schemas["RaidRuntime"] = obj({"status": {"type": "string", "enum": ["None", "Preparing", "Joining", "InRaid", "Finished"]}, "raidId": nullable_guid})
schemas["PlayerRuntime"] = obj({"login": ref("LoginSession"), "currentPartyId": nullable_guid,
    "matching": ref("MatchingRuntime"), "raid": ref("RaidRuntime")})
schemas["PlayerData"] = obj({"schemaVersion": {**integer, "enum": [1]}, "profile": ref("PlayerProfile"),
    "blackSite": ref("BlackSite"), "crafting": array(ref("CraftingJob")), "inventory": ref("Snapshot"), "runtime": ref("PlayerRuntime")})
paths["/api/players/me"] = {"get": {"operationId": "currentPlayerData",
    "description": "Authenticated own-player aggregate. Persisted sections use the inventory/matching player lock. Experience/revisions are decimal strings. Crafting contains only active/unclaimed jobs; ReadyToClaim is calculated from server UTC without granting items. Login describes this token, not online presence. Party and raid assignment are reserved for future trusted server workflows; a Reserved matching entry is not an active raid. Read-only: there is no client snapshot/progression overwrite endpoint.",
    "responses": {"200": response(ref("PlayerData"), "Own player data, no password/token hashes or raw auth token"),
        "401": errors["401"], "404": response(ref("ApiError"), "Player not found"), "503": errors["503"]}}}
spec = {"openapi": "3.0.3", "info": {"title": "DeltaForce Backend", "version": "1.3.0",
    "description": "M1 authoritative lobby backend. Contracts/README.md contains integration rules and boundaries."},
    "servers": [{"url": "http://127.0.0.1:5099"}], "security": [{"Session": []}], "paths": paths,
    "components": {"securitySchemes": {"Session": {"type": "http", "scheme": "bearer", "description": "Opaque login session; not JWT. Lost on API restart; never put in a Travel URL."}}, "schemas": schemas}}
output = root / "Contracts/OpenApi/backend.v1.json"
output.write_text(json.dumps(spec, indent=2) + "\n", encoding="utf-8", newline="\n")

def request(name, method, path, tests, body=None, auth=False):
    value = {"method": method, "url": "{{baseUrl}}" + path,
             "header": [{"key": "Authorization", "value": "Bearer {{token}}"}] if auth else []}
    if body is not None:
        value["header"].append({"key": "Content-Type", "value": "application/json"})
        value["body"] = {"mode": "raw", "raw": json.dumps(body), "options": {"raw": {"language": "json"}}}
    return {"name": name, "request": value, "event": [{"listen": "test", "script": {"type": "text/javascript", "exec": tests}}]}
collection = {
    "info": {"name": "DeltaForce lobby M1", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
             "description": "Local runnable checks derived from backend.v1.json. Requires baseUrl/userId/password environment values. Run with --no-report-events."},
    "item": [
        request("Ready", "GET", "/health/ready", ['pm.test("MSSQL ready", () => { pm.response.to.have.status(200); pm.expect(pm.response.json().database).to.eql("MSSQL"); });']),
        request("Login", "POST", "/api/auth/login", ['pm.test("login success", () => pm.response.to.have.status(200));',
            'pm.test("opaque session", () => pm.expect(pm.response.json().token).to.match(/^[a-f0-9]{64}$/));',
            'pm.collectionVariables.set("token", pm.response.json().token);'], {"userId": "{{userId}}", "password": "{{password}}"}),
        request("Inventory", "GET", "/api/inventory", ['pm.test("snapshot success", () => pm.response.to.have.status(200));',
            'pm.test("versioned authoritative snapshot", () => { const b=pm.response.json(); pm.expect(b.revision).to.match(/^[1-9][0-9]*$/); pm.expect(b.definitionVersion).to.match(/^sha256:[a-f0-9]{64}$/); pm.expect(b.inventoryScope).to.eql("persistent:self"); });'], auth=True),
        request("Own player data", "GET", "/api/players/me", [
            'pm.test("own player aggregate", () => { pm.response.to.have.status(200); const b=pm.response.json(); pm.expect(b.schemaVersion).to.eql(1); pm.expect(b.profile.playerId).to.match(/^[a-f0-9-]{36}$/); pm.expect(b.profile.experience).to.match(/^(0|[1-9][0-9]*)$/); pm.expect(b.blackSite).to.have.property("stashLevel"); pm.expect(b.inventory.inventoryScope).to.eql("persistent:self"); pm.expect(b.runtime.login.isLoggedIn).to.eql(true); });',
            'pm.test("no credentials in player data", () => { const text=pm.response.text(); pm.expect(text).not.to.include("passwordHash"); pm.expect(text).not.to.include("\\\"token\\\""); });'], auth=True),
        request("Anonymous denied", "GET", "/api/inventory", ['pm.test("no anonymous inventory", () => pm.response.to.have.status(401));']),
        request("Logout", "POST", "/api/auth/logout", ['pm.test("session revoked", () => pm.response.to.have.status(204));'], auth=True),
        request("Revoked token denied", "GET", "/api/inventory", ['pm.test("revoked token rejected", () => pm.response.to.have.status(401));'], auth=True),
    ]}
reserve_check = request("Reserve raid", "POST", "/api/raids/reservations", [
    'pm.test("reservation accepted", () => { pm.response.to.have.status(201); pm.expect(pm.response.json().status).to.eql("Reserved"); });',
    'pm.collectionVariables.set("raidId", pm.response.json().raidId); pm.collectionVariables.set("entryId", pm.response.json().entryId);'],
    {"requestId": "{{matchRequestId}}", "mapId": "beacon"}, auth=True)
reserve_check["event"].insert(0, {"listen": "prerequest", "script": {"type": "text/javascript", "exec": [
    'pm.collectionVariables.set("matchRequestId", pm.variables.replaceIn("{{$guid}}"));',
    'pm.collectionVariables.set("cancelRequestId", pm.variables.replaceIn("{{$guid}}"));']}})
collection["item"][3:3] = [reserve_check,
    request("Current raid entry", "GET", "/api/raids/current-entry", [
        'pm.test("own reservation recovered", () => { pm.response.to.have.status(200); pm.expect(pm.response.json().entryId).to.eql(pm.collectionVariables.get("entryId")); });'], auth=True),
    request("Cancel raid", "POST", "/api/raids/{{raidId}}/entry/cancel", [
        'pm.test("reservation cancelled", () => { pm.response.to.have.status(200); pm.expect(pm.response.json().status).to.eql("Cancelled"); });'],
        {"requestId": "{{cancelRequestId}}", "entryId": "{{entryId}}"}, auth=True),
    request("Cancelled entry not active", "GET", "/api/raids/current-entry", [
        'pm.test("no active reservation after cancellation", () => pm.response.to.have.status(404));'], auth=True)]
output = root / "Contracts/Postman/lobby.postman_collection.json"
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(collection, indent=2) + "\n", encoding="utf-8", newline="\n")
print(f"Generated OpenAPI ({len(paths)} paths) and local Postman collection.")
