using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mof.Http.Tests;

public sealed class ProcessTests
{
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
            BinaryDirectory = sandbox.InstallWorker(), TempDirectory = sandbox.PathFor("requests"),
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
            var busy = await Assert.ThrowsAsync<UnwrapException>(() => unwrapper.UnwrapAsync(request, default));
            Assert.Equal(503, busy.StatusCode);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.True(process.HasExited);
            sandbox.AssertClean();

            using var nextSource = new MemoryStream(Encoding.UTF8.GetBytes("#logs"));
            var next = new UnwrapRequest { File = new FormFile(nextSource, 0, nextSource.Length, "File", "mesh.obj") };
            Assert.NotEmpty(await unwrapper.UnwrapAsync(next, default));
            sandbox.AssertClean();
        }
        finally
        {
            cancellation.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
    }
}
