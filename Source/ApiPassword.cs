using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

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
        if (!entries.TryGetValue(key, out var entry))
        {
            return false;
        }
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
            {
                entry.BlockedUntil = now + BlockDuration;
            }
        }
    }

    public void NoteSuccess(string key) => entries.TryRemove(key, out _);
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
    internal const string CookieScheme = "MofCookie";
    internal const string HeaderScheme = "MofHeader";
    internal const string CombinedScheme = "MofPassword";
    internal const string SessionCookieName = "mof_session";
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    internal static ClaimsPrincipal SharedPrincipal => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "shared-password")], CookieScheme));

    public static IApplicationBuilder UseMofPasswordAuth(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<MofSettings>();
        if (string.IsNullOrEmpty(settings.ApiPassword))
        {
            return app;
        }
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
            if (context.User.Identity?.IsAuthenticated == true)
            {
                tracker.NoteSuccess(client);
                await next();
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

            // The login screen's own dependencies must load without credentials.
            // They previously challenged with Basic, so merely opening the login page
            // popped the browser's native sign-in dialog over the custom form.
            if (IsAnonymousAsset(context.Request))
            {
                await next();
                return;
            }

            if (IsBrowserDocumentRequest(context.Request))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(
                    Path.Combine(context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootPath, "login.html"),
                    context.RequestAborted);
                return;
            }

            tracker.NoteFailure(client);
            if (tracker.IsBlocked(client, out retryAfter))
            {
                await TooManyAttemptsAsync(context, retryAfter);
                return;
            }
            await context.ChallengeAsync(CombinedScheme);
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
            && keys.Any(key => Matches(key, expected)))
        {
            return true;
        }

        if (!request.Headers.TryGetValue("Authorization", out var credentials))
        {
            return false;
        }

        foreach (var credential in credentials)
        {
            if (string.IsNullOrEmpty(credential))
            {
                continue;
            }

            if (credential.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                if (Matches(credential["Bearer ".Length..].Trim(), expected))
                {
                    return true;
                }
            }
            else if (credential.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                string decoded;
                try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(credential["Basic ".Length..].Trim())); }
                catch (FormatException) { continue; }
                // The username is ignored; the password follows the first colon (RFC 7617).
                var colon = decoded.IndexOf(':');
                if (Matches(colon < 0 ? decoded : decoded[(colon + 1)..], expected))
                {
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool MatchesPassword(string? candidate, string? expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return true;
        }

        return Matches(candidate, Encoding.UTF8.GetBytes(expected));
    }

    internal static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static bool IsAnonymousAsset(HttpRequest request)
    {
        if (!request.Method.Equals(HttpMethods.Get, StringComparison.OrdinalIgnoreCase)
            && !request.Method.Equals(HttpMethods.Head, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (request.Path.Equals("/login.html", StringComparison.OrdinalIgnoreCase)
            || request.Path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return request.Path.StartsWithSegments("/lib", StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments("/js", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool AcceptsHtml(HttpRequest request) =>
        request.Headers["Accept"].ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    private static bool IsApiPath(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || request.Path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase);

    private static bool IsBrowserDocumentRequest(HttpRequest request)
    {
        if (!request.Method.Equals(HttpMethods.Get, StringComparison.OrdinalIgnoreCase)
            && !request.Method.Equals(HttpMethods.Head, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // API and OpenAPI callers always get JSON so tooling keeps working; every other
        // document navigation (/, /index.html, /swagger, deep links) gets the in-app
        // login screen instead of a Basic challenge that pops the native dialog.
        if (IsApiPath(request))
        {
            return false;
        }
        return AcceptsHtml(request);
    }

    private static bool Matches(string? candidate, byte[] expected)
    {
        if (candidate is null)
        {
            return false;
        }
        var bytes = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(bytes, expected);
    }
}

/// <summary>Authenticates the existing Basic, Bearer, and X-API-Key shared-password formats.</summary>
public sealed class SharedPasswordHeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var password = Context.RequestServices.GetRequiredService<MofSettings>().ApiPassword;
        if (!string.IsNullOrEmpty(password)
            && ApiPassword.IsAuthorized(Request, Encoding.UTF8.GetBytes(password)))
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "shared-password")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }
}

/// <summary>Combines the shared-password headers with ASP.NET Core's protected cookie handler.</summary>
public sealed class CombinedAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = await Context.AuthenticateAsync(ApiPassword.HeaderScheme);
        if (header.Succeeded)
        {
            return header;
        }
        return await Context.AuthenticateAsync(ApiPassword.CookieScheme);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        // Browsers pop a native sign-in dialog for any 401 carrying a Basic challenge,
        // including subresources of the custom login page (its CSS, favicon, ...).
        // API clients send credentials preemptively and parse the JSON body, so only
        // advertise the Basic scheme where the caller is not expecting an HTML document.
        if (!ApiPassword.AcceptsHtml(Request))
        {
            Response.Headers.WWWAuthenticate = "Basic realm=\"MOF\", charset=\"UTF-8\"";
        }
        return Response.WriteAsJsonAsync(
            new { title = "A password is required to use this service.", status = 401 },
            cancellationToken: Context.RequestAborted);
    }
}

public sealed record LoginRequest(string? Password);
