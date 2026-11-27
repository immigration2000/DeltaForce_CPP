# Backend v1 / future UE C++ boundary

`OpenApi/backend.v1.json` is the committed HTTP contract. `Server/tools/build_contract.py` generates it and the small runnable `Postman/lobby.postman_collection.json`. If DTOs change, update the generator and regenerate in the same change. `Definitions/inventory.v1.json` is the immutable server definition manifest. `TestVectors/placement.v1.json` is language-neutral input for both C# and future UE C++ tests.

`.gitattributes` enforces LF for Contracts files so SHA-256 definition versions stay identical across Windows/Linux checkouts.

M1 conventions:

- JSON camelCase is case-sensitive. Unknown request properties and integer enums are rejected.
- GUIDs use lowercase hyphenated UUID strings. DefinitionId and gameplay tags retain their original case and spelling.
- Revision is a positive decimal int64 **string**, scoped to the authenticated player's entire persistent inventory (`persistent:self`).
- Cells are integers. Grid origin is top-left, X right/Y down, index starts at 0. Occupancy is `[x,x+w) × [y,y+h)`; rotation swaps width and height.
- A Grid has containerId, gridIndex, cellPosition and rotated. Its slotTag is null. A slot has slotTag, null containerId, gridIndex=-1, (0,0) and rotated=false. Omitted slot position normalizes to (0,0).
- Only the server confirms state. The client sends intent, source, destination, expectedRevision and definitionVersion; never a complete replacement inventory.
- Same player/requestId + same canonical payload returns the original stored result. The same ID with a different payload is rejected. Records are durable and do not expire in M1.
- Status and HTTP errors are documented in OpenAPI. A transport-level malformed JSON 400 may have no body; 401/503 use ApiError. Valid commands (including rule rejections) use OperationResult. Lookup replays its original status, while a missing record is 404 ApiError.
- Apply the whole Delta once at baseRevision. `updatedItems` are full upserts. `containerChanges` update accessibility; `slotChanges` can clear a slot with null itemId. Resynchronize on a revision gap.
- Merge removes the fully consumed source GUID and records a tombstone. Split generates one new GUID; retries return the same GUID.

C# Domain code is not directly shared with C++. Preserve this contract, manifest version and test vectors when implementing UE transport, preview and replication. UE UI should use one ItemManager interface while its transport changes between lobby HTTP and future raid RPC/replication.

The current UE module contains no implementation of this HTTP client yet. Raid entry/escrow/claim/settlement, protected slots, durability gameplay and server-to-server authentication are future work, not M1 endpoints.
