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

/// <summary>Short-lived in-memory sessions created by the browser login screen.</summary>
public sealed class AuthSessionStore
{
    private readonly ConcurrentDictionary<string, DateTime> sessions = new(StringComparer.Ordinal);

    public string Create(TimeSpan lifetime)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        sessions[token] = DateTime.UtcNow.Add(lifetime);
        return token;
    }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token) || !sessions.TryGetValue(token, out var expiresAt)) return false;
        if (expiresAt > DateTime.UtcNow) return true;
        sessions.TryRemove(token, out _);
        return false;
    }

    public void Remove(string? token)
    {
        if (!string.IsNullOrEmpty(token)) sessions.TryRemove(token, out _);
    }
}

/// <summary>
/// Optional shared-password protection for the UI and API. When <see cref="MofSettings.ApiPassword"/>
/// is set, every request except <c>/health</c> must present the password as HTTP Basic credentials
/// (username is ignored), a Bearer token, an <c>X-API-Key</c> header, or a browser session created
/// by the in-app login screen. Comparisons use constant time.
/// </summary>
public static class ApiPassword
{
    internal const string HealthPath = "/health";
    internal const string ApiKeyHeader = "X-API-Key";
    internal const string SessionCookieName = "mof_session";
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    public static IApplicationBuilder UseMofPasswordAuth(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<MofSettings>();
        if (string.IsNullOrEmpty(settings.ApiPassword)) return app;
        var expected = Encoding.UTF8.GetBytes(settings.ApiPassword);
        var tracker = app.ApplicationServices.GetRequiredService<AuthAttemptTracker>();
        var sessions = app.ApplicationServices.GetRequiredService<AuthSessionStore>();
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
            if (IsAuthorized(context.Request, expected)
                || sessions.IsValid(context.Request.Cookies[SessionCookieName]))
            {
                tracker.NoteSuccess(client);
                await next();
                return;
            }

            if (IsBrowserNavigation(context.Request))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(
                    Path.Combine(context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootPath, "login.html"),
                    context.RequestAborted);
                return;
            }

            // The login form handles its own failure response so fetch() never receives
            // a Basic challenge that could trigger the browser's native password dialog.
            if (context.Request.Method.Equals(HttpMethods.Post, StringComparison.OrdinalIgnoreCase)
                && context.Request.Path.Equals("/login", StringComparison.OrdinalIgnoreCase))
            {
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
        if (request.Headers.TryGetValue(ApiKeyHeader, out var keys)
            && keys.Any(key => Matches(key, expected))) return true;
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

    internal static bool MatchesPassword(string? candidate, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return true;
        return Matches(candidate, Encoding.UTF8.GetBytes(expected));
    }

    internal static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static bool IsBrowserNavigation(HttpRequest request)
    {
        if (!request.Method.Equals(HttpMethods.Get, StringComparison.OrdinalIgnoreCase)
            || (!request.Path.Equals("/", StringComparison.OrdinalIgnoreCase)
                && !request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))) return false;
        return request.Headers["Accept"].ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(string? candidate, byte[] expected)
    {
        if (candidate is null) return false;
        var bytes = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(bytes, expected);
    }
}

public sealed record LoginRequest(string? Password);
