using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mof.Http.Tests;

public sealed class AvailabilityTests
{
    [Fact]
    public async Task StartupSurvivesInternalDownloadCancellation()
    {
        using var sandbox = new Sandbox();
        using var handler = new StubHandler(() => throw new TaskCanceledException("download timed out"));
        using var client = new HttpClient(handler);
        var settings = new MofSettings { BinaryDirectory = sandbox.PathFor("binary") };
        var installer = new BinaryInstaller(settings, new TestEnvironment(sandbox.Root), new TestClients(client),
            NullLogger<BinaryInstaller>.Instance);
        var startup = new BinaryStartup(installer, NullLogger<BinaryStartup>.Instance);

        await startup.StartAsync(CancellationToken.None);

        Assert.False(File.Exists(installer.ExecutablePath));
        Assert.Empty(Directory.GetFileSystemEntries(installer.DirectoryPath));
    }

    [Fact]
    public async Task OnDemandInternalDownloadCancellationReturns503()
    {
        using var sandbox = new Sandbox();
        using var handler = new StubHandler(() => throw new TaskCanceledException("download timed out"));
        using var client = new HttpClient(handler);
        var settings = new MofSettings
        {
            BinaryDirectory = sandbox.PathFor("binary"),
            TempDirectory = sandbox.PathFor("requests")
        };
        var installer = new BinaryInstaller(settings, new TestEnvironment(sandbox.Root), new TestClients(client),
            NullLogger<BinaryInstaller>.Instance);
        using var unwrapper = new Unwrapper(installer, settings, NullLogger<Unwrapper>.Instance);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("v 0 0 0\nf 1 1 1\n"));
        var request = new UnwrapRequest { File = new FormFile(source, 0, source.Length, "File", "mesh.obj") };

        var error = await Assert.ThrowsAsync<UnwrapException>(() => unwrapper.UnwrapAsync(request, CancellationToken.None));

        Assert.Equal(503, error.StatusCode);
        Assert.Empty(Directory.GetFileSystemEntries(installer.DirectoryPath));
    }
}
