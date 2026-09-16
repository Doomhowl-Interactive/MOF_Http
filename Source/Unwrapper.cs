using System.Diagnostics;
using System.Text;

namespace Mof.Http;

public sealed class Unwrapper(BinaryInstaller installer, MofSettings settings, ILogger<Unwrapper> logger) : IDisposable
{
    private readonly SemaphoreSlim slots = new(settings.MaxConcurrentProcesses, settings.MaxConcurrentProcesses);

    public async Task<byte[]> UnwrapAsync(UnwrapRequest request, CancellationToken cancellationToken)
    {
        if (request.File.Length > settings.MaxUploadBytes)
            throw new UnwrapException(413, "The OBJ exceeds the configured upload limit.");
        if (!await slots.WaitAsync(0, cancellationToken))
            throw new UnwrapException(503, "All unwrap processes are busy. Retry later.");
        var directory = Path.Combine(Path.GetFullPath(settings.TempDirectory), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var input = Path.Combine(directory, "input.obj");
            var output = Path.Combine(directory, "output.obj");
            await using (var source = request.File.OpenReadStream())
            await using (var target = File.Create(input))
                await LimitedCopy.CopyAsync(source, target, settings.MaxUploadBytes, cancellationToken);

            var start = MofProcess.CreateStartInfo(settings, installer.ExecutablePath, directory, request);
            using var process = new Process { StartInfo = start };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            process.Start();
            var stdout = DrainAsync(process.StandardOutput);
            var stderr = DrainAsync(process.StandardError);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { /* Already exited. */ }
                }
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                cancellationToken.ThrowIfCancellationRequested();
                throw new UnwrapException(504, "Ministry of Flat exceeded the configured processing timeout.");
            }
            var logs = await Task.WhenAll(stdout, stderr);
            // The bundled MoF 3.x console returns 1 after a successful save.
            // Require an actual UV-bearing mesh as well; exit status alone is insufficient.
            if (process.ExitCode is not (0 or 1) || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                logger.LogWarning("Ministry of Flat failed with exit code {ExitCode}. stdout: {Stdout} stderr: {Stderr}", process.ExitCode, logs[0], logs[1]);
                throw new UnwrapException(422, "Ministry of Flat could not unwrap this OBJ. Check the mesh geometry.");
            }
            if (new FileInfo(output).Length > settings.MaxOutputBytes)
                throw new UnwrapException(422, "The generated OBJ exceeds the configured output limit.");
            var bytes = await File.ReadAllBytesAsync(output, cancellationToken);
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            if (!lines.Any(line => line.StartsWith("vt ", StringComparison.Ordinal))
                || !lines.Any(line => line.StartsWith("f ", StringComparison.Ordinal) && line.Contains('/')))
                throw new UnwrapException(422, "Ministry of Flat did not produce a mesh with UV coordinates.");
            return bytes;
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException exception) { logger.LogWarning(exception, "Could not clean temporary directory {Path}", directory); }
            catch (UnauthorizedAccessException exception) { logger.LogWarning(exception, "Could not clean temporary directory {Path}", directory); }
            slots.Release();
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var log = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (log.Length < 16384) log.Append(buffer, 0, Math.Min(count, 16384 - log.Length));
        return log.ToString();
    }

    public void Dispose() => slots.Dispose();
}

public sealed class UnwrapException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
