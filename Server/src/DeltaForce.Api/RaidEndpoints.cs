using System.Security.Claims;
using DeltaForce.Application;

namespace DeltaForce.Api;

public static class RaidEndpoints
{
    public static void MapRaidReservations(this RouteGroupBuilder api)
    {
        var raids = api.MapGroup("/raids").RequireAuthorization();
        raids.MapPost("/reservations", async (ClaimsPrincipal user, ReserveRaidRequest request, IRaidReservationService service, CancellationToken ct)
            => Reply(await service.ReserveAsync(PlayerId(user), request, ct)));
        raids.MapGet("/current-entry", async (ClaimsPrincipal user, IRaidReservationService service, CancellationToken ct)
            => Entry(await service.GetCurrentAsync(PlayerId(user), ct)));
        raids.MapGet("/{raidId:guid}/entry", async (ClaimsPrincipal user, Guid raidId, IRaidReservationService service, CancellationToken ct)
            => Entry(await service.GetEntryAsync(PlayerId(user), raidId, ct)));
        raids.MapPost("/{raidId:guid}/entry/cancel", async (ClaimsPrincipal user, Guid raidId, CancelRaidRequest request, IRaidReservationService service, CancellationToken ct)
            => Reply(await service.CancelAsync(PlayerId(user), raidId, request, ct)));
        raids.MapGet("/requests/{requestId:guid}", async (ClaimsPrincipal user, Guid requestId, IRaidReservationService service, CancellationToken ct) =>
        {
            var response = await service.GetResultAsync(PlayerId(user), requestId, ct);
            return response == null ? Results.NotFound(new ApiError("RequestNotRecorded")) : Reply(response);
        });
    }
    private static Guid PlayerId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static IResult Reply(RaidResponse response) => response.Reservation is { } reservation
        ? Results.Json(reservation, statusCode: response.StatusCode) : Results.Json(response.Error, statusCode: response.StatusCode);
    private static IResult Entry(RaidReservation? reservation) => reservation == null
        ? Results.NotFound(new ApiError("EntryNotFound")) : Results.Ok(reservation);
}
