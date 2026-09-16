using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mof.Http.Tests;

public sealed class InstallerTests
{
    private static byte[] Archive(params (string Name, string Body)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (name, body) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(body);
            }
        return buffer.ToArray();
    }

    private static BinaryInstaller Installer(Sandbox sandbox, HttpClient client) => new(
        new MofSettings { BinaryDirectory = sandbox.PathFor("binary") }, new TestEnvironment(sandbox.Root),
        new TestClients(client), NullLogger<BinaryInstaller>.Instance);

    [WindowsFact]
    public async Task ExistingBinarySkipsNetwork()
    {
        using var sandbox = new Sandbox();
        sandbox.InstallWorker();
        using var handler = new StubHandler(() => throw new Exception("Must not download"));
        using var client = new HttpClient(handler);
        await Installer(sandbox, client).EnsureInstalledAsync(default);
        Assert.Equal(0, handler.Calls);
    }

    [WindowsFact]
    public async Task ConcurrentInstallRequestsDownloadOnlyOnceAndCopyCompanions()
    {
        using var sandbox = new Sandbox();
        var archive = Archive(("release/UnWrapConsole3.exe", "fixture"), ("release/helper.dll", "companion"));
        using var handler = new StubHandler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        using var client = new HttpClient(handler);
        var installer = Installer(sandbox, client);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => installer.EnsureInstalledAsync(default)));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("fixture", await File.ReadAllTextAsync(installer.ExecutablePath));
        Assert.Equal("companion", await File.ReadAllTextAsync(Path.Combine(installer.DirectoryPath, "helper.dll")));
        Assert.Empty(Directory.GetDirectories(installer.DirectoryPath));
    }

    [WindowsFact]
    public async Task HttpFailureLeavesNoInstalledBinaryAndCanBeRetried()
    {
        using var sandbox = new Sandbox();
        var fail = true;
        using var handler = new StubHandler(() => fail ? new(HttpStatusCode.ServiceUnavailable) :
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(("UnWrapConsole3.exe", "fixture"))) });
        using var client = new HttpClient(handler);
        var installer = Installer(sandbox, client);
        await Assert.ThrowsAsync<HttpRequestException>(() => installer.EnsureInstalledAsync(default));
        Assert.Empty(Directory.GetFileSystemEntries(installer.DirectoryPath));
        fail = false;
        await installer.EnsureInstalledAsync(default);
        Assert.True(File.Exists(installer.ExecutablePath));
    }

    [WindowsFact]
    public async Task InvalidArchivesLeaveNoInstalledBinary()
    {
        foreach (var bytes in new[] { new byte[] { 1, 2, 3 }, Archive(("readme.txt", "no executable")), Archive(("UnWrapConsole3.exe", "")), Archive(("../escaped.exe", "bad"), ("UnWrapConsole3.exe", "fixture")) })
        {
            using var sandbox = new Sandbox();
            using var handler = new StubHandler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            using var client = new HttpClient(handler);
            var installer = Installer(sandbox, client);
            var error = await Record.ExceptionAsync(() => installer.EnsureInstalledAsync(default));
            Assert.True(error is InvalidDataException or IOException, $"Unexpected error: {error}");
            Assert.Empty(Directory.GetFileSystemEntries(installer.DirectoryPath));
            Assert.False(File.Exists(sandbox.PathFor("escaped.exe")));
        }
    }
}
