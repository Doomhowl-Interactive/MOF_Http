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
    return settings;
});
builder.Services.AddOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>()
    .Configure<MofSettings>((options, settings) => options.Limits.MaxRequestBodySize = settings.MaxUploadBytes + 64 * 1024);
builder.Services.AddOptions<FormOptions>()
    .Configure<MofSettings>((options, settings) => options.MultipartBodyLengthLimit = settings.MaxUploadBytes + 64 * 1024);
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient("download", client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<BinaryInstaller>();
builder.Services.AddHostedService<BinaryStartup>();
builder.Services.AddSingleton<Unwrapper>();
builder.Services.AddMofOpenApi();
var app = builder.Build();
app.UseExceptionHandler();
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
app.MapControllers();
app.MapOpenApi();
app.MapGet("/health", (BinaryInstaller installer) => Results.Ok(new { status = File.Exists(installer.ExecutablePath) ? "ready" : "unavailable" }))
    .WithSummary("Check gateway readiness.");
app.Run();

public partial class Program;
