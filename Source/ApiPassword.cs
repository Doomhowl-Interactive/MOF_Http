using System.Security.Cryptography;
using System.Text;

namespace Mof.Http;

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
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals(HealthPath, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }
            if (IsAuthorized(context.Request, expected))
            {
                await next();
                return;
            }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"MOF\", charset=\"UTF-8\"";
            await context.Response.WriteAsJsonAsync(
                new { title = "A password is required to use this service.", status = 401 },
                cancellationToken: context.RequestAborted);
        });
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
