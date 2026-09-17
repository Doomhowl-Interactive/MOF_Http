using System.ComponentModel;
using System.Reflection;
using Microsoft.OpenApi;

namespace Mof.Http;

internal static class OpenApiConfiguration
{
    internal static IServiceCollection AddMofOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
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
                var settings = context.ApplicationServices.GetRequiredService<MofSettings>();
                if (!string.IsNullOrEmpty(settings.ApiPassword))
                {
                    operation.Responses["401"] = new OpenApiResponse { Description = "Missing or incorrect API password." };
                    operation.Responses["429"] = new OpenApiResponse { Description = "Too many password attempts. Retry later." };
                }
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
            options.AddDocumentTransformer((document, context, _) =>
            {
                document.Info.Title = "Ministry of Flat HTTP Gateway";
                document.Info.Version = "v1";
                document.Info.Description = "For private use only. Upload an OBJ and receive its UV-unwrapped OBJ in the same HTTP response. Numeric form fields use invariant notation (a dot for decimals).";
                var settings = context.ApplicationServices.GetRequiredService<MofSettings>();
                if (string.IsNullOrEmpty(settings.ApiPassword)) return Task.CompletedTask;
                document.AddComponent("basic", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "basic",
                    Description = "The shared API password (username is ignored)."
                });
                document.AddComponent("bearer", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "The shared API password as a bearer token."
                });
                document.AddComponent("apiKey", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    Name = "X-API-Key",
                    In = ParameterLocation.Header,
                    Description = "The shared API password in the X-API-Key header."
                });
                document.Security ??= [];
                document.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("basic", document)] = [],
                    [new OpenApiSecuritySchemeReference("bearer", document)] = [],
                    [new OpenApiSecuritySchemeReference("apiKey", document)] = []
                });
                return Task.CompletedTask;
            });
        });

        return services;
    }
}
