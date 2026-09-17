using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Mof.Http;

/// <summary>
/// Tracks failed password attempts per client IP and temporarily blocks brute force.
/// Registered as a singleton so each application instance gets an isolated tracker.
/// </summary>
public sealed class AuthAttemptTracker
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private sealed class Entry
    {
        public readonly List<DateTime> Failures = [];
        public DateTime? BlockedUntil;
    }

    /// <summary>Failures within <see cref="Window"/> before a temporary block. Default 20.</summary>
    public int MaxFailures { get; set; } = 20;
    /// <summary>Sliding window for counting failures. Default 5 minutes.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>How long a client is blocked with 429 once the threshold is hit. Default 1 minute.</summary>
    public TimeSpan BlockDuration { get; set; } = TimeSpan.FromMinutes(1);

    public bool IsBlocked(string key, out TimeSpan retryAfter)
    {
        retryAfter = default;
        if (!entries.TryGetValue(key, out var entry)) return false;
        lock (entry)
        {
            if (entry.BlockedUntil.HasValue)
            {
                var now = DateTime.UtcNow;
                if (now < entry.BlockedUntil.Value)
                {
                    retryAfter = entry.BlockedUntil.Value - now;
                    return true;
                }
                entry.BlockedUntil = null;
            }
            return false;
        }
    }

    public void NoteFailure(string key)
    {
        var entry = entries.GetOrAdd(key, _ => new());
        lock (entry)
        {
            var now = DateTime.UtcNow;
            entry.Failures.RemoveAll(time => now - time > Window);
            entry.Failures.Add(now);
            if (entry.Failures.Count >= MaxFailures)
                entry.BlockedUntil = now + BlockDuration;
        }
    }

    public void NoteSuccess(string key) => entries.TryRemove(key, out _);
}

/// <summary>
/// Optional shared-password protection for the UI and API. When <see cref="MofSettings.ApiPassword"/>
/// is set, every request except <c>/health</c> must present the password as HTTP Basic credentials
/// (username is ignored), a Bearer token, or an <c>X-API-Key</c> header. Comparisons use constant time.
/// </summary>
public static class ApiPassword
{
    internal const string HealthPath = "/health";
    internal const string ApiKeyHeader = "X-API-Key";

    public static IApplicationBuilder UseMofPasswordAuth(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<MofSettings>();
        if (string.IsNullOrEmpty(settings.ApiPassword)) return app;
        var expected = Encoding.UTF8.GetBytes(settings.ApiPassword);
        var tracker = app.ApplicationServices.GetRequiredService<AuthAttemptTracker>();
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals(HealthPath, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }
            var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (tracker.IsBlocked(client, out var retryAfter))
            {
                await TooManyAttemptsAsync(context, retryAfter);
                return;
            }
            if (IsAuthorized(context.Request, expected))
            {
                tracker.NoteSuccess(client);
                await next();
                return;
            }
            tracker.NoteFailure(client);
            if (tracker.IsBlocked(client, out retryAfter))
            {
                await TooManyAttemptsAsync(context, retryAfter);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"MOF\", charset=\"UTF-8\"";
            await context.Response.WriteAsJsonAsync(
                new { title = "A password is required to use this service.", status = 401 },
                cancellationToken: context.RequestAborted);
        });
    }

    private static Task TooManyAttemptsAsync(HttpContext context, TimeSpan retryAfter)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return context.Response.WriteAsJsonAsync(
            new { title = "Too many password attempts. Retry later.", status = 429 },
            cancellationToken: context.RequestAborted);
    }

    internal static bool IsAuthorized(HttpRequest request, byte[] expected)
    {
        if (request.Headers.TryGetValue(ApiKeyHeader, out var keys))
            foreach (var key in keys)
                if (Matches(key, expected)) return true;
        if (!request.Headers.TryGetValue("Authorization", out var credentials)) return false;
        foreach (var credential in credentials)
        {
            if (string.IsNullOrEmpty(credential)) continue;
            if (credential.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                if (Matches(credential["Bearer ".Length..].Trim(), expected)) return true;
            }
            else if (credential.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                string decoded;
                try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(credential["Basic ".Length..].Trim())); }
                catch (FormatException) { continue; }
                // The username is ignored; the password follows the first colon (RFC 7617).
                var colon = decoded.IndexOf(':');
                if (Matches(colon < 0 ? decoded : decoded[(colon + 1)..], expected)) return true;
            }
        }
        return false;
    }

    private static bool Matches(string? candidate, byte[] expected)
    {
        if (candidate is null) return false;
        var bytes = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(bytes, expected);
    }
}
