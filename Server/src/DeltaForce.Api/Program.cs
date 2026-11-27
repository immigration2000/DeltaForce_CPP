using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using DeltaForce.Api;
using DeltaForce.Application;
using DeltaForce.Domain;
using DeltaForce.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    var contract = ContractJson.Options;
    o.SerializerOptions.PropertyNamingPolicy = contract.PropertyNamingPolicy;
    o.SerializerOptions.PropertyNameCaseInsensitive = false;
    o.SerializerOptions.UnmappedMemberHandling = contract.UnmappedMemberHandling;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
    o.SerializerOptions.MaxDepth = 32;
    foreach (var converter in contract.Converters) o.SerializerOptions.Converters.Add(converter);
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024);
byte[] manifestBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Definitions", "inventory.v1.json"));
var manifest = JsonSerializer.Deserialize<DefinitionManifest>(manifestBytes, ContractJson.Options)
    ?? throw new InvalidOperationException("Definition manifest is missing");
var catalog = new DefinitionCatalog(manifest, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton<InventoryEngine>();
builder.Services.AddDbContext<InventoryDbContext>(o => o.UseSqlServer(
    builder.Configuration.GetConnectionString("Inventory") ?? throw new InvalidOperationException("Inventory connection is required")));
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddSingleton(new RaidMaps(builder.Configuration.GetSection("Raid:MapIds").Get<string[]>() ?? ["beacon"]));
builder.Services.AddScoped<IRaidReservationService, RaidReservationService>();
builder.Services.AddSingleton<PasswordHasher<PlayerRow>>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddAuthentication("Session").AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>("Session", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    if (error is BadHttpRequestException bad)
    {
        context.Response.StatusCode = bad.StatusCode;
        await context.Response.WriteAsJsonAsync(new ApiError("InvalidRequest"));
        return;
    }
    context.Response.StatusCode = 503;
    await context.Response.WriteAsJsonAsync(new ApiError("DependencyUnavailable", true));
}));
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/health/live", () => Results.Ok(new { status = "Alive" }));
app.MapGet("/health/ready", async (InventoryDbContext db, CancellationToken ct) =>
{
    await db.Players.AsNoTracking().Select(p => p.PlayerId).Take(1).ToListAsync(ct);
    return Results.Ok(new { status = "Ready", database = "MSSQL" });
});
app.MapGet("/openapi/v1.json", () => Results.File(Path.Combine(AppContext.BaseDirectory, "Contracts", "backend.v1.json"), "application/json"));
var api = app.MapGroup("/api/v1");
api.MapRaidReservations();
api.MapPost("/auth/login", async (LoginRequest request, IAccountService accounts, SessionStore sessions, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.UserId) || request.UserId.Length > 64 || string.IsNullOrEmpty(request.Password) || request.Password.Length > 128)
        return Results.Json(new ApiError("InvalidRequest"), statusCode: 400);
    var player = await accounts.VerifyAsync(request.UserId, request.Password, ct);
    return player == null ? Results.Json(new ApiError("InvalidCredentials"), statusCode: 401) : Results.Ok(sessions.Create(player));
}).RequireRateLimiting("login");
api.MapPost("/auth/logout", (HttpContext context, SessionStore sessions) =>
{
    sessions.Remove(SessionAuthenticationHandler.ReadToken(context.Request));
    return Results.NoContent();
}).RequireAuthorization();
api.MapGet("/definitions", () => Results.Ok(new { definitionVersion = catalog.Version, manifest = catalog.Manifest })).RequireAuthorization();
api.MapGet("/inventory", async (ClaimsPrincipal user, IInventoryService inventory, CancellationToken ct) =>
{
    var snapshot = await inventory.GetAsync(PlayerId(user), ct);
    return snapshot == null ? Results.NotFound(new ApiError("PlayerNotFound")) : Results.Ok(snapshot);
}).RequireAuthorization();
api.MapPost("/inventory/operations", async (ClaimsPrincipal user, InventoryCommand command,
    IInventoryService inventory, ILoggerFactory loggers, CancellationToken ct) =>
{
    var response = await inventory.ExecuteAsync(PlayerId(user), command, ct);
    loggers.CreateLogger("InventoryOperations").LogInformation("Inventory {RequestId} {PlayerId} {Status} {ErrorCode}",
        command.RequestId, PlayerId(user), response.Result.Status, response.Result.ErrorCode);
    return Results.Json(response.Result, statusCode: response.StatusCode);
}).RequireAuthorization();
api.MapGet("/inventory/operations/{requestId:guid}", async (ClaimsPrincipal user, Guid requestId, IInventoryService inventory, CancellationToken ct) =>
{
    var response = await inventory.GetResultAsync(PlayerId(user), requestId, ct);
    return response == null ? Results.NotFound(new ApiError("OperationNotRecorded"))
        : Results.Json(response.Result, statusCode: response.StatusCode);
}).RequireAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
    if (builder.Configuration.GetValue("Database:Initialize", app.Environment.IsDevelopment()))
        await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment() && builder.Configuration["Development:SeedPassword"] is { Length: > 0 } seedPassword)
        await DevelopmentSeeder.EnsureAsync(db, catalog, seedPassword,
            resetExistingPasswords: builder.Configuration.GetValue("Development:ResetDemoPasswords", false));
}
await app.RunAsync();

static Guid PlayerId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
public partial class Program { }
