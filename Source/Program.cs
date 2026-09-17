using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Mof.Http;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var settings = configuration.GetSection("MinistryOfFlat").Get<MofSettings>() ?? new();
    // The password lives in the root ApiPassword key (the ApiPassword environment variable),
    // falling back to MinistryOfFlat:ApiPassword when the root key is unset.
    settings.ApiPassword = configuration["ApiPassword"] ?? settings.ApiPassword;
    settings.Validate();
    // Resolve relative directories against the content root so the gateway behaves the same
    // regardless of the process working directory (native run vs. container vs. tests).
    var environment = services.GetRequiredService<IHostEnvironment>();
    settings.BinaryDirectory = Path.GetFullPath(settings.BinaryDirectory, environment.ContentRootPath);
    settings.TempDirectory = Path.GetFullPath(settings.TempDirectory, environment.ContentRootPath);
    return settings;
});
builder.Services.AddOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>()
    .Configure<MofSettings>((options, _) => options.Limits.MaxRequestBodySize = null);
builder.Services.AddOptions<FormOptions>()
    .Configure<MofSettings>((options, settings) => options.MultipartBodyLengthLimit = settings.MaxUploadBytes + 64 * 1024);
builder.Services.AddControllers();
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    // Let the unwrap action inspect ModelState itself so a multipart body over the
    // upload limit can be reported as 413 instead of the default 400.
    options.SuppressModelStateInvalidFilter = true;
});
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        // Backstop for any direct 413 from request handling; the multipart-limit case is
        // mapped in the unwrap action. Keeps a single wording for "upload too large".
        if (context.Exception is BadHttpRequestException bad && bad.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            context.ProblemDetails.Status = StatusCodes.Status413PayloadTooLarge;
            context.ProblemDetails.Title = "The OBJ exceeds the configured upload limit.";
        }
    };
});
builder.Services.AddSingleton<AuthAttemptTracker>();
builder.Services.AddAuthentication(ApiPassword.CombinedScheme)
    .AddCookie(ApiPassword.CookieScheme, options =>
    {
        options.Cookie.Name = ApiPassword.SessionCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = ApiPassword.SessionLifetime;
        options.SlidingExpiration = false;
    })
    .AddScheme<AuthenticationSchemeOptions, SharedPasswordHeaderAuthenticationHandler>(
        ApiPassword.HeaderScheme, _ => { })
    .AddScheme<AuthenticationSchemeOptions, CombinedAuthenticationHandler>(
        ApiPassword.CombinedScheme, _ => { });
builder.Services.AddHttpClient("download", client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<BinaryInstaller>();
builder.Services.AddHostedService<BinaryStartup>();
builder.Services.AddSingleton<Unwrapper>();
builder.Services.AddMofOpenApi();
var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseMofPasswordAuth();
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture),
    RequestCultureProviders = []
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("../openapi/v1.json", "Ministry of Flat API v1");
    options.DocumentTitle = "Ministry of Flat API";
});
app.MapPost("/login", async (LoginRequest login, HttpContext context, MofSettings settings, AuthAttemptTracker tracker) =>
{
    if (string.IsNullOrEmpty(settings.ApiPassword)) return Results.NotFound();

    var client = ApiPassword.ClientKey(context);
    if (tracker.IsBlocked(client, out var retryAfter))
    {
        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return Results.Json(new { title = "Too many password attempts. Retry later.", status = 429 }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (!ApiPassword.MatchesPassword(login.Password, settings.ApiPassword))
    {
        tracker.NoteFailure(client);
        if (tracker.IsBlocked(client, out retryAfter))
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return Results.Json(new { title = "Too many password attempts. Retry later.", status = 429 }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        return Results.Json(new { title = "The password was not accepted.", status = 401 }, statusCode: StatusCodes.Status401Unauthorized);
    }

    tracker.NoteSuccess(client);
    await context.SignInAsync(ApiPassword.CookieScheme, ApiPassword.SharedPrincipal, new AuthenticationProperties
    {
        IsPersistent = true,
        AllowRefresh = false,
        ExpiresUtc = DateTimeOffset.UtcNow.Add(ApiPassword.SessionLifetime)
    });
    return Results.Ok(new { redirect = "/" });
}).ExcludeFromDescription();
app.MapControllers();
app.MapOpenApi();
app.MapGet("/health", (BinaryInstaller installer) => Results.Ok(new { status = File.Exists(installer.ExecutablePath) ? "ready" : "unavailable" }))
    .WithSummary("Check gateway readiness.");
await app.RunAsync();

public partial class Program
{
    protected Program() { }
}
