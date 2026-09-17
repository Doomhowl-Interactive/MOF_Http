using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mof.Http.Tests;

public sealed class ProcessTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("/usr/bin/wine")]
    public void LauncherPreservesExecutableAndUsesPortableMeshPaths(string? wine)
    {
        var settings = new MofSettings { WineExecutable = wine };
        const string executable = "/binary with spaces/UnWrapConsole3.exe";
        const string directory = "/requests with spaces/job";
        var request = new UnwrapRequest { Aspect = 1.5, Separate = true };
        var start = MofProcess.CreateStartInfo(settings, executable, directory, request);
        Assert.Equal(wine ?? executable, start.FileName);
        Assert.Equal(directory, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        var expected = request.Arguments("input.obj", "output.obj");
        Assert.Equal(wine is null ? expected : new[] { executable }.Concat(expected), start.ArgumentList);
    }

    [Fact]
    public void ArgumentsAreInvariantAndKeepPathsAsIndividualArguments()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-BE");
            var arguments = new UnwrapRequest { Aspect = 1.5, CenterX = -2.25, Separate = true }.Arguments("C:/a b/input.obj", "C:/a b/output.obj");
            Assert.Equal("C:/a b/input.obj", arguments[0]);
            Assert.Contains("1.5", arguments);
            Assert.Contains("-2.25", arguments);
            Assert.Contains("TRUE", arguments);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [WindowsFact]
    public async Task CancellationTerminatesProcessReleasesCapacityAndCleansFiles()
    {
        using var sandbox = new Sandbox();
        var settings = new MofSettings
        {
            BinaryDirectory = sandbox.InstallWorker(),
            TempDirectory = sandbox.PathFor("requests"),
            MaxConcurrentProcesses = 1
        };
        using var client = new HttpClient();
        var installer = new BinaryInstaller(settings, new TestEnvironment(sandbox.Root), new TestClients(client), NullLogger<BinaryInstaller>.Instance);
        using var unwrapper = new Unwrapper(installer, settings, NullLogger<Unwrapper>.Instance);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("#delay"));
        var request = new UnwrapRequest { File = new FormFile(source, 0, source.Length, "File", "mesh.obj") };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = unwrapper.UnwrapAsync(request, cancellation.Token);
        try
        {
            string? pidFile = null;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                pidFile = Directory.GetFiles(settings.TempDirectory, "started.pid", SearchOption.AllDirectories).FirstOrDefault();
                if (pidFile is not null && new FileInfo(pidFile).Length > 0) break;
                await Task.Delay(20);
            }
            Assert.NotNull(pidFile);
            using var process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile)));
            using var queuedSource = new MemoryStream(Encoding.UTF8.GetBytes("#logs"));
            var queuedRequest = new UnwrapRequest { File = new FormFile(queuedSource, 0, queuedSource.Length, "File", "mesh.obj") };
            using var waitingCancellation = new CancellationTokenSource();
            var canceledWaiter = unwrapper.UnwrapAsync(queuedRequest, waitingCancellation.Token);
            var queued = unwrapper.UnwrapAsync(queuedRequest, default);
            Assert.False(canceledWaiter.IsCompleted);
            Assert.False(queued.IsCompleted);
            waitingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.True(process.HasExited);
            using (var completed = await queued)
                Assert.True(new FileInfo(completed.ContentPath).Length > 0);
            sandbox.AssertClean();
        }
        finally
        {
            cancellation.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
    }
}
