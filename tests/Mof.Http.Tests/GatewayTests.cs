using System.Net;
using System.Text;
using System.Text.Json;

namespace Mof.Http.Tests;

public sealed class GatewayTests
{
    internal const string Cube = "v -1 -1 -1\nv 1 -1 -1\nv 1 1 -1\nv -1 1 -1\nv -1 -1 1\nv 1 -1 1\nv 1 1 1\nv -1 1 1\nf 1 4 3 2\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n";

    internal static MultipartFormDataContent Form(string text = Cube, string filename = "cube.obj", string? field = null, string? value = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "File", filename);
        if (field is not null) form.Add(new StringContent(value!), field);
        return form;
    }

    [WindowsFact]
    public async Task RealExecutableReturnsMeshWithUvCoordinatesAndCleansFiles()
    {
        using var sandbox = new Sandbox();
        using var factory = new GatewayFactory(new() { ["MinistryOfFlat:TempDirectory"] = sandbox.PathFor("requests") });
        using var client = factory.CreateClient();
        using var form = Form(field: "Resolution", value: "512");
        using var response = await client.PostAsync("/api/unwrap", form);
        var output = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, output);
        Assert.Contains("\nvt ", output);
        Assert.Contains("\nf ", output);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        sandbox.AssertClean();
    }

    [WindowsFact]
    public async Task InvalidGeometryReturns422AndCleansFiles()
    {
        using var sandbox = new Sandbox();
        using var factory = new GatewayFactory(new() { ["MinistryOfFlat:TempDirectory"] = sandbox.PathFor("requests") });
        using var client = factory.CreateClient();
        using var form = Form("this is not a mesh\n");
        using var response = await client.PostAsync("/api/unwrap", form);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        sandbox.AssertClean();
    }

    [WindowsFact]
    public async Task OpenApiDescribesFileSettingsDefaultsAndResponses()
    {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/unwrap").GetProperty("post");
        var schema = operation.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), item => item.GetString() == "File");
        var properties = schema.GetProperty("properties");
        Assert.Equal("binary", properties.GetProperty("File").GetProperty("format").GetString());
        Assert.Equal(1024, properties.GetProperty("Resolution").GetProperty("default").GetInt32());
        Assert.Contains("island gaps", properties.GetProperty("Resolution").GetProperty("description").GetString());
        Assert.True(operation.GetProperty("responses").TryGetProperty("504", out _));
        Assert.Contains("ready", await client.GetStringAsync("/health"));
    }

    [WindowsFact]
    public async Task IndexPageProvidesObjDropZone()
    {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();
        var page = await client.GetStringAsync("/");
        Assert.Contains("id=\"drop-zone\"", page);
        Assert.Contains("fetch(\"/api/unwrap\"", page);
    }

    [Theory]
    [InlineData("bad.txt", "Resolution", "512")]
    [InlineData("cube.obj", "Resolution", "0")]
    [InlineData("cube.obj", "Aspect", "NaN")]
    [InlineData("cube.obj", "CenterX", "Infinity")]
    [InlineData("cube.obj", "Udims", "-1")]
    [InlineData("cube.obj", "Separate", "maybe")]
    public async Task InvalidInputsReturn400(string filename, string field, string value)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();
        using var form = Form(filename: filename, field: field, value: value);
        using var response = await client.PostAsync("/api/unwrap", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [WindowsFact]
    public async Task MissingAndEmptyFilesReturn400()
    {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();
        using var missing = new MultipartFormDataContent();
        missing.Add(new StringContent("512"), "Resolution");
        using var missingResponse = await client.PostAsync("/api/unwrap", missing);
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
        using var empty = Form("");
        using var emptyResponse = await client.PostAsync("/api/unwrap", empty);
        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);
    }

    [WindowsFact]
    public async Task UploadLimitReturns413()
    {
        using var factory = new GatewayFactory(new() { ["MinistryOfFlat:MaxUploadBytes"] = "10" });
        using var client = factory.CreateClient();
        using var form = Form();
        using var response = await client.PostAsync("/api/unwrap", form);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [WindowsFact]
    public async Task MultipartLimitReturns413Problem()
    {
        using var factory = new GatewayFactory(new() { ["MinistryOfFlat:MaxUploadBytes"] = "10" });
        using var client = factory.CreateClient();
        using var form = Form(new string('v', 80000));
        using var response = await client.PostAsync("/api/unwrap", form);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("upload limit", body, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public async Task ProcessFailuresTimeoutAndOutputLimitReturnDocumentedErrors()
    {
        using var sandbox = new Sandbox();
        using var factory = new GatewayFactory(new()
        {
            ["MinistryOfFlat:BinaryDirectory"] = sandbox.InstallWorker(),
            ["MinistryOfFlat:TempDirectory"] = sandbox.PathFor("requests"),
            ["MinistryOfFlat:TimeoutSeconds"] = "1",
            ["MinistryOfFlat:MaxOutputBytes"] = "100"
        });
        using var client = factory.CreateClient();
        foreach (var (text, status) in new[] { ("#fail", 422), ("#nooutput", 422), ("#delay", 504), ("#large", 422), ("#logs", 200) })
        {
            using var form = Form(text);
            using var response = await client.PostAsync("/api/unwrap", form);
            Assert.Equal(status, (int)response.StatusCode);
            sandbox.AssertClean();
        }
    }

    [DownloadFact]
    public async Task FreshOfficialDownloadStartsServerAndUnwraps()
    {
        using var sandbox = new Sandbox();
        using var factory = new GatewayFactory(new()
        {
            ["MinistryOfFlat:BinaryDirectory"] = sandbox.PathFor("downloaded"),
            ["MinistryOfFlat:TempDirectory"] = sandbox.PathFor("requests")
        });
        using var client = factory.CreateClient();
        Assert.True(File.Exists(sandbox.PathFor("downloaded/UnWrapConsole3.exe")));
        using var form = Form();
        using var response = await client.PostAsync("/api/unwrap", form);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("\nvt ", body);
        sandbox.AssertClean();
    }
}
