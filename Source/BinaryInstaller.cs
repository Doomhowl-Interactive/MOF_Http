using System.IO.Compression;

namespace Mof.Http;

public sealed class BinaryInstaller(MofSettings settings, IHostEnvironment environment,
    IHttpClientFactory clients, ILogger<BinaryInstaller> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public string DirectoryPath { get; } = Path.GetFullPath(settings.BinaryDirectory, environment.ContentRootPath);
    public string ExecutablePath => Path.Combine(DirectoryPath, "UnWrapConsole3.exe");

    public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Ministry of Flat requires Windows or Linux with Wine.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(ExecutablePath)) return;
            Directory.CreateDirectory(DirectoryPath);
            var stage = Path.Combine(DirectoryPath, ".download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                logger.LogInformation("Downloading Ministry of Flat from {Url}", settings.DownloadUrl);
                using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                downloadTimeout.CancelAfter(TimeSpan.FromMinutes(5));
                using var response = await clients.CreateClient("download").GetAsync(settings.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead, downloadTimeout.Token);
                response.EnsureSuccessStatusCode();
                var archivePath = Path.Combine(stage, "release.zip");
                await using (var source = await response.Content.ReadAsStreamAsync(downloadTimeout.Token))
                await using (var target = File.Create(archivePath))
                    await LimitedCopy.CopyAsync(source, target, 100 * 1024 * 1024, downloadTimeout.Token);

                using var archive = ZipFile.OpenRead(archivePath);
                if (archive.Entries.Sum(entry => entry.Length) > 500L * 1024 * 1024)
                    throw new InvalidDataException("Release archive exceeds the extraction limit.");
                // ZipFile rejects paths escaping this staging directory.
                var extracted = Path.Combine(stage, "extracted");
                archive.ExtractToDirectory(extracted);
                var executables = Directory.GetFiles(extracted, "UnWrapConsole3.exe", SearchOption.AllDirectories);
                if (executables.Length != 1 || new FileInfo(executables[0]).Length == 0)
                    throw new InvalidDataException("Release must contain exactly one nonempty UnWrapConsole3.exe.");
                var sourceDirectory = Path.GetDirectoryName(executables[0])!;
                foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
                {
                    if (file == executables[0]) continue;
                    var destination = Path.Combine(DirectoryPath, Path.GetRelativePath(sourceDirectory, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, overwrite: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Publish the executable last: interrupted downloads are never considered installed.
                File.Move(executables[0], ExecutablePath, overwrite: true);
                logger.LogInformation("Ministry of Flat is ready at {Path}", ExecutablePath);
            }
            finally { Directory.Delete(stage, recursive: true); }
        }
        finally { gate.Release(); }
    }
}

public sealed class BinaryStartup(BinaryInstaller installer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => installer.EnsureInstalledAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class LimitedCopy
{
    public static async Task CopyAsync(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("File exceeds the configured size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
