using System.ComponentModel;
using System.Reflection;
using Microsoft.OpenApi;
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Mof.Http;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(services =>
{
    var settings = services.GetRequiredService<IConfiguration>().GetSection("MinistryOfFlat").Get<MofSettings>() ?? new();
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
builder.Services.AddOpenApi(options =>
{
    options.AddOperationTransformer((operation, context, _) =>
    {
        if (context.Description.RelativePath != "api/unwrap") return Task.CompletedTask;
        var schema = operation.RequestBody!.Content!["multipart/form-data"].Schema!;
        foreach (var property in typeof(UnwrapRequest).GetProperties())
        {
            if (!schema.Properties!.TryGetValue(property.Name, out var propertySchema)) continue;
            if (propertySchema is OpenApiSchema mutable)
                mutable.Description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
        }
        schema.Properties!["File"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary", Description = "Nonempty Wavefront OBJ mesh (.obj)." };
        if (schema is OpenApiSchema formSchema) formSchema.Required = new HashSet<string> { "File" };
        operation.Responses!["200"].Content!["application/octet-stream"].Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
        return Task.CompletedTask;
    });
    options.AddSchemaTransformer((schema, context, _) =>
    {
        var attributes = context.JsonPropertyInfo?.AttributeProvider?.GetCustomAttributes(true);
        if (attributes?.OfType<DescriptionAttribute>().FirstOrDefault() is { } description)
            schema.Description = description.Description;
        if (attributes?.OfType<DefaultValueAttribute>().FirstOrDefault() is { } defaultValue)
            schema.Default = System.Text.Json.JsonSerializer.SerializeToNode(defaultValue.Value);
        return Task.CompletedTask;
    });
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Ministry of Flat HTTP Gateway";
        document.Info.Version = "v1";
        document.Info.Description = "For private use only. Upload an OBJ and receive its UV-unwrapped OBJ in the same HTTP response. Numeric form fields use invariant notation (a dot for decimals).";
        return Task.CompletedTask;
    });
});
var app = builder.Build();
app.UseExceptionHandler();
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture),
    RequestCultureProviders = []
});
app.MapControllers();
app.MapOpenApi();
app.MapGet("/health", (BinaryInstaller installer) => Results.Ok(new { status = File.Exists(installer.ExecutablePath) ? "ready" : "unavailable" }))
    .WithSummary("Check gateway readiness.");
app.Run();

public partial class Program;
