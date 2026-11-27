using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using DeltaForce.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DeltaForce.Api;

public sealed class SessionStore
{
    private sealed class Entry(AuthenticatedPlayer player, DateTimeOffset loggedInAt, DateTimeOffset expiresAt)
    {
        public AuthenticatedPlayer Player { get; } = player;
        public DateTimeOffset LoggedInAt { get; } = loggedInAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public long LastSeenTicks = loggedInAt.UtcTicks;
    }
    private readonly ConcurrentDictionary<string, Entry> sessions = new();
    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public LoginResponse Create(AuthenticatedPlayer player)
    {
        foreach (var pair in sessions.Where(p => p.Value.ExpiresAt <= DateTimeOffset.UtcNow)) sessions.TryRemove(pair.Key, out _);
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddHours(8);
        sessions[Hash(token)] = new(player, now, expires);
        return new(player.PlayerId, player.UserId, token, expires);
    }
    public AuthenticatedPlayer? Find(string token)
    {
        if (token.Length != 64) return null;
        string key = Hash(token);
        if (!sessions.TryGetValue(key, out var entry)) return null;
        if (entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            Interlocked.Exchange(ref entry.LastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);
            return entry.Player;
        }
        sessions.TryRemove(key, out _);
        return null;
    }
    public void Remove(string token) => sessions.TryRemove(Hash(token), out _);
    public LoginSessionView? Describe(string token)
    {
        if (!sessions.TryGetValue(Hash(token), out var entry) || entry.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        return new(true, entry.LoggedInAt, entry.ExpiresAt,
            new DateTimeOffset(Interlocked.Read(ref entry.LastSeenTicks), TimeSpan.Zero));
    }
}

public sealed class SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, SessionStore sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public static string ReadToken(HttpRequest request)
    {
        string header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
    }
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string token = ReadToken(Request);
        if (token.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var player = sessions.Find(token);
        if (player == null) return Task.FromResult(AuthenticateResult.Fail("SessionExpired"));
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, player.PlayerId.ToString("D")),
            new(ClaimTypes.Name, player.UserId)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
    }
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new ApiError("SessionExpired"));
    }
}
