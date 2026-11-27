using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeltaForce.Application;
using DeltaForce.Domain;
using DeltaForce.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

if (args.Length < 2) throw new ArgumentException("Usage: IntegrationChecks <dotnet.exe> <Api.dll> [--keep-database]");
string dotnet = Path.GetFullPath(args[0]), apiDll = Path.GetFullPath(args[1]);
string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(apiDll)!, "../../../../../../"));
string database = "DeltaForce_Check_" + Guid.NewGuid().ToString("N");
var sql = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DELTAFORCE_TEST_SQL")
    ?? "Server=.\\SQLEXPRESS;Integrated Security=True;Encrypt=True;TrustServerCertificate=True") { InitialCatalog = "master" };
string masterConnection = sql.ConnectionString;
sql.InitialCatalog = database;
string connection = sql.ConnectionString;
string password = "1234";
int firstPort = FreePort(), secondPort = FreePort();
var processes = new List<Process>();
var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
int passed = 0;
bool created = false;
try
{
    await Sql($"CREATE DATABASE [{database}]", masterConnection);
    created = true;
    var first = await Start(firstPort, true);
    var second = await Start(secondPort, false);
    using var anonymous = Client(firstPort);
    Expect((int)(await anonymous.GetAsync("/api/v1/inventory")).StatusCode == 401, "anonymous inventory denied");
    var wrong = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("demo", "incorrect-password"), ContractJson.Options);
    Expect((int)wrong.StatusCode == 401, "wrong password denied");
    var caseMismatch = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("DEMO", password), ContractJson.Options);
    Expect((int)caseMismatch.StatusCode == 401, "account ID preserves case");
    using var a = await Login(firstPort, "demo");
    using var aOtherProcess = await Login(secondPort, "demo");
    using var b = await Login(firstPort, "demo2");
    var start = await Snapshot(a);
    var other = await Snapshot(b);
    Expect(start.Items.Length == 7 && start.Containers.Length == 2 && start.Revision == "1", "fresh account snapshot and version");
    Expect(!start.Items.Select(i => i.ItemId).Intersect(other.Items.Select(i => i.ItemId)).Any(), "account item identities isolated");
    Guid stash = start.Containers.Single(c => c.OwnerItemId == null).ContainerId;
    Guid helmet = start.Items.Single(i => i.DefinitionId == "11010006002").ItemId;
    Guid armor = start.Items.Single(i => i.DefinitionId == "11050005001").ItemId;
    Guid bag = start.Items.Single(i => i.DefinitionId == "11080001001").ItemId;
    Guid ammo = start.Items.Single(i => i.Quantity == 30).ItemId;

    var firstMove = Command(start, ammo, InventoryLocation.Grid(stash, 3, 8));
    var firstResponse = await Execute(a, firstMove, 200);
    Expect(firstResponse.Result.Delta!.ScopeChanges.Single().UpdatedItems.Length == 1
        && firstResponse.Result.NewRevisions.Single().Revision == "2", "move commits a single-item delta");
    var equipped = await Change(helmet, InventoryLocation.Slot("Slot.Helmet"), OperationKind.Equip);
    Expect(equipped.Result.Delta!.ScopeChanges.Single().SlotChanges.Single().ItemId == helmet, "equipment keeps item ID and updates slot");
    await Change(helmet, InventoryLocation.Grid(stash, 0, 10), OperationKind.Unequip);
    var bagSnapshot = await Snapshot(a);
    var bagLocation = bagSnapshot.Items.Single(i => i.ItemId == bag).Location;
    await Execute(a, Command(bagSnapshot, bag, bagLocation with { Rotated = true }, OperationKind.Rotate), 200);
    await Change(bag, InventoryLocation.Slot("Slot.Backpack"), OperationKind.Equip);
    Expect((await Snapshot(a)).Containers.Single(c => c.ContainerId == bag).IsAccessible, "equipped bag becomes accessible");
    await Change(ammo, InventoryLocation.Grid(bag, 0, 0));
    var beforeCycle = await Snapshot(a);
    await Execute(a, Command(beforeCycle, bag, InventoryLocation.Grid(bag, 0, 0)), 422, "CycleDetected");
    Expect(Same(beforeCycle, await Snapshot(a)), "cycle rejection leaves all state unchanged");
    await Change(bag, InventoryLocation.Grid(stash, 7, 10), OperationKind.Unequip);
    var storedBag = await Snapshot(a);
    await Execute(a, Command(storedBag, ammo, InventoryLocation.Grid(stash, 3, 8)), 403, "ContainerAccessDenied");
    Expect(Same(storedBag, await Snapshot(a)), "stored bag contents preserved and protected");
    await Change(bag, InventoryLocation.Slot("Slot.Backpack"), OperationKind.Equip);
    await Change(ammo, InventoryLocation.Grid(stash, 3, 8));
    var beforeSwap = await Snapshot(a);
    var swap = await Execute(a, Command(beforeSwap, helmet, beforeSwap.Items.Single(i => i.ItemId == armor).Location,
        OperationKind.Swap, armor), 200);
    Expect(swap.Result.Delta!.ScopeChanges.Single().UpdatedItems.Length == 2, "grid swap applies both items atomically");
    await Change(helmet, InventoryLocation.Grid(stash, 7, 20));
    var failureSnapshot = await Snapshot(a);
    await Execute(a, Command(failureSnapshot, armor, failureSnapshot.Items.Single(i => i.ItemId == helmet).Location,
        OperationKind.Swap, helmet), 422, "OutOfBounds");
    Expect(Same(failureSnapshot, await Snapshot(a)), "failed swap changes neither item nor revision");
    await Execute(a, Command(failureSnapshot, helmet, InventoryLocation.Grid(stash, 0, 10)), 422, "Collision");
    await Execute(a, Command(failureSnapshot, helmet, InventoryLocation.Slot("Slot.Armor"), OperationKind.Equip), 422, "CategoryMismatch");
    await Execute(a, Command(failureSnapshot, helmet, InventoryLocation.Grid(stash, int.MaxValue, 0)), 422, "OutOfBounds");
    await Execute(a, Command(failureSnapshot, helmet, InventoryLocation.Grid(stash, 6, 20)) with { DefinitionVersion = "wrong" }, 409, "DefinitionMismatch");
    await Execute(a, Command(failureSnapshot, helmet, InventoryLocation.Grid(stash, 6, 20)) with
        { ExpectedRevisions = [new(InventoryViews.Scope, "1")] }, 409, "RevisionMismatch");
    var foreignItem = other.Items[0];
    await Execute(a, new(Guid.NewGuid(), OperationKind.Move, foreignItem.ItemId, foreignItem.Location,
        InventoryLocation.Grid(stash, 6, 20), [new(InventoryViews.Scope, failureSnapshot.Revision)], failureSnapshot.DefinitionVersion), 404, "ItemNotFound");
    Expect(Same(failureSnapshot, await Snapshot(a)) && Same(other, await Snapshot(b)), "invalid and foreign requests leave both accounts unchanged");
    var forged = JsonSerializer.SerializeToNode(Command(failureSnapshot, helmet, InventoryLocation.Grid(stash, 6, 20)), ContractJson.Options)!;
    forged["playerId"] = other.InventoryScope;
    using var forgedResponse = await a.PostAsync("/api/v1/inventory/operations", new StringContent(forged.ToJsonString(), Encoding.UTF8, "application/json"));
    Expect((int)forgedResponse.StatusCode == 400, "unrecognized owner override rejected");

    var replay = await Execute(aOtherProcess, firstMove, 200);
    Expect(Same(firstResponse.Result, replay.Result) && Same(failureSnapshot, await Snapshot(a)), "old successful request replays before revision check across processes");
    await Execute(a, firstMove with { Destination = InventoryLocation.Grid(stash, 4, 8) }, 409, "RequestPayloadMismatch");
    var lookup = await a.GetFromJsonAsync<OperationResult>($"/api/v1/inventory/operations/{firstMove.RequestId}", ContractJson.Options);
    Expect(Same(firstResponse.Result, lookup!), "request result can be queried");
    Expect((int)(await b.GetAsync($"/api/v1/inventory/operations/{firstMove.RequestId}")).StatusCode == 404, "another player cannot read request result");

    var splitCommand = Command(await Snapshot(a), ammo, InventoryLocation.Grid(stash, 4, 8), OperationKind.Split, quantity: 5);
    var split = await Execute(a, splitCommand, 200);
    Guid splitId = split.Result.Delta!.ScopeChanges.Single().AddedItems.Single().ItemId;
    var splitReplay = await Execute(aOtherProcess, splitCommand, 200);
    Expect(Same(split.Result, splitReplay.Result), "split retry preserves generated GUID");
    var splitSnapshot = await Snapshot(a);
    await Execute(a, Command(splitSnapshot, splitId, splitSnapshot.Items.Single(i => i.ItemId == ammo).Location, OperationKind.Merge, ammo), 200);
    Expect((await Snapshot(a)).Items.Where(i => i.DefinitionId == "37100200001").Sum(i => i.Quantity) == 40, "split and merge conserve stack quantity");
    Expect(await Scalar($"SELECT COUNT(*) FROM [RetiredItems] WHERE [ItemId] = '{splitId:D}'") == 1, "fully merged GUID is tombstoned");

    // Failure occurs after item/location writes, at the operation record insert.
    await Sql("CREATE TRIGGER [FailOperationInsert] ON [OperationRecords] AFTER INSERT AS BEGIN THROW 51000, 'Injected test failure', 1; END", connection);
    var rollbackBefore = await Snapshot(a);
    var retryAfterRollback = Command(rollbackBefore, helmet, InventoryLocation.Grid(stash, 6, 20));
    var failedCommit = await a.PostAsJsonAsync("/api/v1/inventory/operations", retryAfterRollback, ContractJson.Options);
    Expect((int)failedCommit.StatusCode == 503, "injected SQL failure reported as retryable dependency error");
    Expect(Same(rollbackBefore, await Snapshot(a)), "failed commit rolls back location, item and revision");
    Expect((int)(await a.GetAsync($"/api/v1/inventory/operations/{retryAfterRollback.RequestId}")).StatusCode == 404, "failed transaction leaves no committed request result");
    await Sql("DROP TRIGGER [FailOperationInsert]", connection);
    await Execute(aOtherProcess, retryAfterRollback, 200);

    var concurrentBefore = await Snapshot(a);
    var duplicate = Command(concurrentBefore, helmet, InventoryLocation.Grid(stash, 7, 20));
    var duplicates = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Execute(i % 2 == 0 ? a : aOtherProcess, duplicate, 200)));
    Expect(duplicates.All(r => Same(r.Result, duplicates[0].Result)), "simultaneous same-key requests return one exact result");
    Expect(long.Parse((await Snapshot(a)).Revision) == long.Parse(concurrentBefore.Revision) + 1, "duplicate concurrency increments revision once");
    var raceBefore = await Snapshot(a);
    var left = Command(raceBefore, helmet, InventoryLocation.Grid(stash, 6, 20));
    var right = Command(raceBefore, helmet, InventoryLocation.Grid(stash, 5, 20));
    var race = await Task.WhenAll(a.PostAsJsonAsync("/api/v1/inventory/operations", left, ContractJson.Options),
        aOtherProcess.PostAsJsonAsync("/api/v1/inventory/operations", right, ContractJson.Options));
    Expect(race.Count(r => (int)r.StatusCode == 200) == 1 && race.Count(r => (int)r.StatusCode == 409) == 1,
        "different requests at same revision have one winner across API processes");
    Expect(long.Parse((await Snapshot(a)).Revision) == long.Parse(raceBefore.Revision) + 1, "revision race does not lose or double-write state");

    var readRaceBefore = await Snapshot(a);
    var currentHelmet = readRaceBefore.Items.Single(i => i.ItemId == helmet);
    var readRaceDestination = currentHelmet.Location with { CellPosition = new(7, 22) };
    var writer = Execute(a, Command(readRaceBefore, helmet, readRaceDestination), 200);
    var readers = Enumerable.Range(0, 8).Select(i => Snapshot(i % 2 == 0 ? a : aOtherProcess)).ToArray();
    await writer;
    var readSnapshots = await Task.WhenAll(readers);
    var readRaceAfter = await Snapshot(a);
    Expect(readSnapshots.All(s => Same(s, readRaceBefore) || Same(s, readRaceAfter)), "concurrent snapshot readers see a complete pre- or post-commit state");

    // Exercise the immediate SQL filtered unique index during equipment slot exchange.
    var weapons = (await Snapshot(a)).Items.Where(i => i.DefinitionId == "18010000006").ToArray();
    await Change(weapons[0].ItemId, InventoryLocation.Slot("Slot.Primary01"), OperationKind.Equip);
    await Change(weapons[1].ItemId, InventoryLocation.Slot("Slot.Primary02"), OperationKind.Equip);
    var beforeEquipmentSwap = await Snapshot(a);
    var equipmentSwap = await Execute(a, Command(beforeEquipmentSwap, weapons[0].ItemId,
        beforeEquipmentSwap.Items.Single(i => i.ItemId == weapons[1].ItemId).Location,
        OperationKind.Swap, weapons[1].ItemId), 200);
    var afterEquipmentSwap = await Snapshot(a);
    Expect(afterEquipmentSwap.Slots.Single(s => s.SlotTag == "Slot.Primary01").ItemId == weapons[1].ItemId
        && afterEquipmentSwap.Slots.Single(s => s.SlotTag == "Slot.Primary02").ItemId == weapons[0].ItemId
        && equipmentSwap.Result.Delta!.ScopeChanges.Single().SlotChanges.Length == 2,
        "equipment swap satisfies SQL unique slots and returns both slot changes");
    Expect(afterEquipmentSwap.Slots.Single(s => s.SlotTag == "Slot.Backpack").ItemId == bag, "equipment state survives unrelated commands");
    var persisted = await Snapshot(a);
    var raidCheckpoint = await RaidChecks.RunAsync(a, aOtherProcess, b, Expect, text => Sql(text, connection));
    await Stop(second); await Stop(first);
    await using (var passwordConnection = new SqlConnection(connection))
    {
        await passwordConnection.OpenAsync();
        var oldHash = new PasswordHasher<PlayerRow>().HashPassword(new PlayerRow(), "previous-demo-password");
        await using var resetFixture = new SqlCommand("UPDATE [Players] SET [PasswordHash] = @hash WHERE [UserId] IN (N'demo', N'demo2')", passwordConnection);
        resetFixture.Parameters.AddWithValue("@hash", oldHash);
        await resetFixture.ExecuteNonQueryAsync();
    }
    first = await Start(firstPort, true, resetPasswords: true);
    Expect((int)(await a.GetAsync("/api/v1/inventory")).StatusCode == 401, "API restart expires in-memory sessions");
    using var restored = await Login(firstPort, "demo");
    var recoveredEntry = await restored.GetFromJsonAsync<RaidReservation>("/api/v1/raids/current-entry", ContractJson.Options);
    Expect(Same(raidCheckpoint.Reservation, recoveredEntry!), "matching reservation survives API restart and login");
    using var matchingReplay = await restored.GetAsync($"/api/v1/raids/requests/{raidCheckpoint.RequestId}");
    Expect((int)matchingReplay.StatusCode == 201 && Same(raidCheckpoint.Reservation,
        (await matchingReplay.Content.ReadFromJsonAsync<RaidReservation>(ContractJson.Options))!), "matching request result survives API restart");
    using var cancelRecovered = await restored.PostAsJsonAsync($"/api/v1/raids/{recoveredEntry!.RaidId}/entry/cancel",
        new CancelRaidRequest(Guid.NewGuid(), recoveredEntry.EntryId), ContractJson.Options);
    Expect((int)cancelRecovered.StatusCode == 200, "recovered matching reservation can be cancelled");
    using var restoredOther = await Login(firstPort, "demo2");
    Expect(Same(other, await Snapshot(restoredOther)), "demo2 password reset preserves its inventory and revision");
    var oldPasswordLogin = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("demo", "previous-demo-password"), ContractJson.Options);
    Expect((int)oldPasswordLogin.StatusCode == 401, "previous demo password rejected after explicit reset to 1234");
    Expect(Same(persisted, await Snapshot(restored)), "API process restart and re-login restore exact inventory");
    var durableReplay = await Execute(restored, firstMove, 200);
    Expect(Same(durableReplay.Result, firstResponse.Result), "request results survive API process restart");
    using var anotherSession = await Login(firstPort, "demo");
    Expect((int)(await restored.PostAsync("/api/v1/auth/logout", null)).StatusCode == 204, "logout succeeds after immediately saved operations");
    Expect((int)(await restored.GetAsync("/api/v1/inventory")).StatusCode == 401, "logout invalidates used session");
    Expect((int)(await anotherSession.GetAsync("/api/v1/inventory")).StatusCode == 200, "logout preserves other sessions");
    using var relogin = await Login(firstPort, "demo");
    Expect(Same(persisted, await Snapshot(relogin)), "logout and re-login restore saved state without snapshot upload");
    await RunPostman(firstPort);
    Console.WriteLine($"PASS: {passed} live MSSQL/HTTP assertions. Database: {database}");
}
catch
{
    Console.Error.WriteLine("API log tail (credentials are never logged):");
    foreach (string line in logs.TakeLast(45)) Console.Error.WriteLine(line);
    throw;
}
finally
{
    foreach (var process in processes) await Stop(process);
    if (created && !args.Contains("--keep-database", StringComparer.Ordinal))
    {
        SqlConnection.ClearAllPools();
        // Only the newly-created, unpredictable test database is eligible for cleanup.
        if (!System.Text.RegularExpressions.Regex.IsMatch(database, "^DeltaForce_Check_[a-f0-9]{32}$")) throw new InvalidOperationException("Unsafe test database name");
        await Sql($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]", masterConnection);
        Console.WriteLine("Removed the isolated test database.");
    }
}

void Expect(bool success, string label)
{
    if (!success) throw new InvalidOperationException("FAIL: " + label);
    Interlocked.Increment(ref passed); Console.WriteLine("PASS " + label);
}
static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, ContractJson.Options) == JsonSerializer.Serialize(b, ContractJson.Options);
HttpClient Client(int port, string? token = null)
{
    var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(25) };
    if (token != null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return client;
}
async Task<HttpClient> Login(int port, string user)
{
    using var client = Client(port);
    using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user, password), ContractJson.Options);
    if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"Login failed: {response.StatusCode}");
    var result = await response.Content.ReadFromJsonAsync<LoginResponse>(ContractJson.Options);
    return Client(port, result!.Token);
}
async Task<InventorySnapshot> Snapshot(HttpClient client)
    => (await client.GetFromJsonAsync<InventorySnapshot>("/api/v1/inventory", ContractJson.Options))!;
InventoryCommand Command(InventorySnapshot snapshot, Guid id, InventoryLocation destination,
    OperationKind operation = OperationKind.Move, Guid? target = null, int? quantity = null)
    => new(Guid.NewGuid(), operation, id, snapshot.Items.Single(i => i.ItemId == id).Location,
        destination, [new(InventoryViews.Scope, snapshot.Revision)], snapshot.DefinitionVersion, target, quantity);
async Task<OperationResponse> Execute(HttpClient client, InventoryCommand command, int expectedStatus, string? error = null)
{
    using var response = await client.PostAsJsonAsync("/api/v1/inventory/operations", command, ContractJson.Options);
    string body = await response.Content.ReadAsStringAsync();
    if ((int)response.StatusCode != expectedStatus) throw new InvalidOperationException($"{command.Operation}: expected {expectedStatus}, got {(int)response.StatusCode}: {body}");
    var result = JsonSerializer.Deserialize<OperationResult>(body, ContractJson.Options)!;
    if (error != null && result.ErrorCode != error) throw new InvalidOperationException($"Expected {error}, got {result.ErrorCode}");
    Interlocked.Increment(ref passed);
    return new(expectedStatus, result);
}
async Task<OperationResponse> Change(Guid id, InventoryLocation destination, OperationKind operation = OperationKind.Move)
{
    // Each new intent starts from the latest confirmed snapshot.
    using var client = await Login(firstPort, "demo");
    return await Execute(client, Command(await Snapshot(client), id, destination, operation), 200);
}
async Task<Process> Start(int port, bool initialize, bool resetPasswords = false)
{
    var info = new ProcessStartInfo(dotnet) { WorkingDirectory = Path.GetDirectoryName(apiDll)!,
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(apiDll); info.ArgumentList.Add("--urls"); info.ArgumentList.Add($"http://127.0.0.1:{port}");
    info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    info.Environment["ConnectionStrings__Inventory"] = connection;
    info.Environment["Database__Initialize"] = initialize ? "true" : "false";
    info.Environment["Development__SeedPassword"] = initialize ? password : "";
    info.Environment["Development__ResetDemoPasswords"] = resetPasswords ? "true" : "false";
    var process = new Process { StartInfo = info };
    process.OutputDataReceived += (_, e) => { if (e.Data != null) logs.Enqueue(e.Data); };
    process.ErrorDataReceived += (_, e) => { if (e.Data != null) logs.Enqueue(e.Data); };
    process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); processes.Add(process);
    using var client = Client(port);
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(45) && !process.HasExited)
    {
        try { if ((await client.GetAsync("/health/ready")).IsSuccessStatusCode) return process; }
        catch (HttpRequestException) { }
        await Task.Delay(150);
    }
    throw new InvalidOperationException("API did not become ready");
}
static async Task Stop(Process process)
{
    if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
}
static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
}
static async Task Sql(string text, string connectionString)
{
    await using var sqlConnection = new SqlConnection(connectionString); await sqlConnection.OpenAsync();
    await using var command = new SqlCommand(text, sqlConnection); await command.ExecuteNonQueryAsync();
}
async Task<int> Scalar(string text)
{
    await using var sqlConnection = new SqlConnection(connection); await sqlConnection.OpenAsync();
    await using var command = new SqlCommand(text, sqlConnection); return Convert.ToInt32(await command.ExecuteScalarAsync());
}
async Task RunPostman(int port)
{
    string collection = Path.Combine(root, "Contracts", "Postman", "lobby.postman_collection.json");
    string environment = Path.Combine(Path.GetTempPath(), "deltaforce-postman-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(environment, JsonSerializer.Serialize(new { name = "Isolated local check", values = new[]
    {
        new { key = "baseUrl", value = $"http://127.0.0.1:{port}", enabled = true },
        new { key = "userId", value = "demo", enabled = true },
        new { key = "password", value = password, enabled = true }
    } }));
    try
    {
        var info = new ProcessStartInfo("postman.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        foreach (string arg in new[] { "collection", "run", collection, "-e", environment, "--no-report-events", "--timeout", "30000" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Console.WriteLine(await output); Console.Error.Write(await errors);
        Expect(process.ExitCode == 0, "saved Postman HTTP contract collection");
    }
    finally { File.Delete(environment); }
}
