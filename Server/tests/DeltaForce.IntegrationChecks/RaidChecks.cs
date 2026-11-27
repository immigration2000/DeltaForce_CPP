using System.Net.Http.Json;
using System.Text.Json;
using DeltaForce.Application;

internal sealed record RaidCheckpoint(RaidReservation Reservation, Guid RequestId);

internal static class RaidChecks
{
    public static async Task<RaidCheckpoint> RunAsync(HttpClient a, HttpClient peer, HttpClient b,
        Action<bool, string> expect, Func<string, Task> sql)
    {
        var before = await a.GetFromJsonAsync<InventorySnapshot>("/api/v1/inventory", ContractJson.Options);
        using var anonymous = new HttpClient { BaseAddress = a.BaseAddress };
        expect((int)(await anonymous.PostAsJsonAsync("/api/v1/raids/reservations", new ReserveRaidRequest(Guid.NewGuid(), "beacon"), ContractJson.Options)).StatusCode == 401,
            "anonymous matching denied");
        expect((int)(await a.GetAsync("/api/v1/raids/current-entry")).StatusCode == 404, "fresh account has no matching reservation");
        await Post(a, "/api/v1/raids/reservations", new ReserveRaidRequest(Guid.NewGuid(), "missing-map"), 400, "InvalidMap");
        var request = new ReserveRaidRequest(Guid.NewGuid(), "beacon");
        var starts = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Post(i % 2 == 0 ? a : peer,
            "/api/v1/raids/reservations", request, 201)));
        var entry = starts[0]!;
        expect(starts.All(r => Same(r!, entry)) && entry.Status == "Reserved", "same-key matching retries create one exact reservation across processes");
        expect(Same(entry, (await a.GetFromJsonAsync<RaidReservation>("/api/v1/raids/current-entry", ContractJson.Options))!), "current entry recovers active reservation");
        expect(Same(entry, (await peer.GetFromJsonAsync<RaidReservation>($"/api/v1/raids/requests/{request.RequestId}", ContractJson.Options))!), "matching result lookup is durable and player scoped");
        await Post(a, "/api/v1/raids/reservations", request with { MapId = "missing-map" }, 409, "RequestPayloadMismatch");
        var conflictingStart = new ReserveRaidRequest(Guid.NewGuid(), "beacon");
        await Post(a, "/api/v1/raids/reservations", conflictingStart, 409, "AlreadyMatching");
        expect((int)(await b.GetAsync($"/api/v1/raids/{entry.RaidId}/entry")).StatusCode == 404
            && (int)(await b.GetAsync($"/api/v1/raids/requests/{request.RequestId}")).StatusCode == 404, "foreign player cannot read matching state or results");
        await Post(b, $"/api/v1/raids/{entry.RaidId}/entry/cancel", new CancelRaidRequest(Guid.NewGuid(), entry.EntryId), 404, "EntryNotFound");
        var cancel = new CancelRaidRequest(Guid.NewGuid(), entry.EntryId);
        var cancelled = await Post(a, $"/api/v1/raids/{entry.RaidId}/entry/cancel", cancel, 200);
        expect(cancelled!.Status == "Cancelled", "matching cancellation commits terminal state");
        expect(Same(cancelled, (await Post(peer, $"/api/v1/raids/{entry.RaidId}/entry/cancel", cancel, 200))!), "cancel same-key replay is exact");
        expect((int)(await a.GetAsync("/api/v1/raids/current-entry")).StatusCode == 404, "cancel releases active reservation slot");
        await Post(a, "/api/v1/raids/reservations", conflictingStart, 409, "AlreadyMatching");
        var newer = (await Post(a, "/api/v1/raids/reservations", new ReserveRaidRequest(Guid.NewGuid(), "beacon"), 201))!;
        await Post(a, $"/api/v1/raids/{entry.RaidId}/entry/cancel", new CancelRaidRequest(Guid.NewGuid(), entry.EntryId), 200);
        expect(Same(newer, (await a.GetFromJsonAsync<RaidReservation>("/api/v1/raids/current-entry", ContractJson.Options))!), "late cancel of old entry preserves newer reservation");
        await Post(a, $"/api/v1/raids/{newer.RaidId}/entry/cancel", new CancelRaidRequest(Guid.NewGuid(), newer.EntryId), 200);
        var race = await Task.WhenAll(a.PostAsJsonAsync("/api/v1/raids/reservations", new ReserveRaidRequest(Guid.NewGuid(), "beacon"), ContractJson.Options),
            peer.PostAsJsonAsync("/api/v1/raids/reservations", new ReserveRaidRequest(Guid.NewGuid(), "beacon"), ContractJson.Options));
        expect(race.Count(r => (int)r.StatusCode == 201) == 1 && race.Count(r => (int)r.StatusCode == 409) == 1,
            "different matching keys racing for one player have one winner");
        var winner = (await race.Single(r => (int)r.StatusCode == 201).Content.ReadFromJsonAsync<RaidReservation>(ContractJson.Options))!;
        await Post(a, $"/api/v1/raids/{winner.RaidId}/entry/cancel", new CancelRaidRequest(Guid.NewGuid(), winner.EntryId), 200);

        const string failTrigger = "CREATE TRIGGER [FailRaidResultInsert] ON [RaidRequestRecords] AFTER INSERT AS BEGIN THROW 51001, 'Injected matching test failure', 1; END";
        await sql(failTrigger);
        var retry = new ReserveRaidRequest(Guid.NewGuid(), "beacon");
        await Post(a, "/api/v1/raids/reservations", retry, 503, "DependencyUnavailable");
        expect((int)(await a.GetAsync("/api/v1/raids/current-entry")).StatusCode == 404
            && (int)(await a.GetAsync($"/api/v1/raids/requests/{retry.RequestId}")).StatusCode == 404,
            "failed result insert rolls back both matching reservation and request record");
        await sql("DROP TRIGGER [FailRaidResultInsert]");
        var retriedEntry = (await Post(peer, "/api/v1/raids/reservations", retry, 201))!;
        await sql(failTrigger);
        var retryCancel = new CancelRaidRequest(Guid.NewGuid(), retriedEntry.EntryId);
        await Post(a, $"/api/v1/raids/{retriedEntry.RaidId}/entry/cancel", retryCancel, 503, "DependencyUnavailable");
        expect((await a.GetFromJsonAsync<RaidReservation>("/api/v1/raids/current-entry", ContractJson.Options))!.Status == "Reserved",
            "failed cancellation transaction preserves active reservation");
        await sql("DROP TRIGGER [FailRaidResultInsert]");
        await Post(peer, $"/api/v1/raids/{retriedEntry.RaidId}/entry/cancel", retryCancel, 200);
        var bEntry = (await Post(b, "/api/v1/raids/reservations", new ReserveRaidRequest(Guid.NewGuid(), "beacon"), 201))!;
        var finalRequest = new ReserveRaidRequest(Guid.NewGuid(), "beacon");
        var finalEntry = (await Post(a, "/api/v1/raids/reservations", finalRequest, 201))!;
        expect(bEntry.EntryId != finalEntry.EntryId, "different accounts can reserve independently");
        await Post(b, $"/api/v1/raids/{bEntry.RaidId}/entry/cancel", new CancelRaidRequest(Guid.NewGuid(), bEntry.EntryId), 200);
        expect(Same(before!, (await a.GetFromJsonAsync<InventorySnapshot>("/api/v1/inventory", ContractJson.Options))!),
            "reservation and cancellation leave inventory and revision unchanged");
        return new(finalEntry, finalRequest.RequestId);

        async Task<RaidReservation?> Post<T>(HttpClient client, string path, T body, int status, string? error = null)
        {
            using var response = await client.PostAsJsonAsync(path, body, ContractJson.Options);
            string text = await response.Content.ReadAsStringAsync();
            if ((int)response.StatusCode != status) throw new InvalidOperationException($"Matching {path}: expected {status}, got {(int)response.StatusCode}: {text}");
            expect(true, $"matching HTTP {status}");
            if (status >= 400)
            {
                var problem = JsonSerializer.Deserialize<ApiError>(text, ContractJson.Options)!;
                expect(problem.ErrorCode == error, "matching error " + error);
                return null;
            }
            return JsonSerializer.Deserialize<RaidReservation>(text, ContractJson.Options)!;
        }
    }

    private static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, ContractJson.Options) == JsonSerializer.Serialize(b, ContractJson.Options);
}
