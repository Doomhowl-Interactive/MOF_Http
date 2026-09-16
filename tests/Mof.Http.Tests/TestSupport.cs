using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mof.Http.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "The native executable requires Windows.";
    }
}

public sealed class DownloadFactAttribute : FactAttribute
{
    public DownloadFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("MOF_TEST_DOWNLOAD") != "1")
            Skip = "Set MOF_TEST_DOWNLOAD=1 on Windows to test the official download.";
    }
}

internal sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Mof.Tests", Guid.NewGuid().ToString("N"));
    public Sandbox() => Directory.CreateDirectory(Root);
    public string PathFor(string name) => Path.Combine(Root, name);
    public string InstallWorker()
    {
        var binary = PathFor("binary");
        Directory.CreateDirectory(binary);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "worker")))
            File.Copy(file, Path.Combine(binary, Path.GetFileName(file)));
        File.Copy(Path.Combine(binary, "Mof.TestWorker.exe"), Path.Combine(binary, "UnWrapConsole3.exe"));
        return binary;
    }
    public void AssertClean()
    {
        var temp = PathFor("requests");
        Assert.True(!Directory.Exists(temp) || !Directory.EnumerateFileSystemEntries(temp).Any(), "Request files were not cleaned up.");
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

internal sealed class GatewayFactory(Dictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides ?? []));
    }
}

internal sealed class TestEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class TestClients(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

internal sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(response());
    }
}
