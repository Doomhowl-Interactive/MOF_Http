using System.Diagnostics;
using System.Text;

namespace Mof.Http;

public sealed class Unwrapper(BinaryInstaller installer, MofSettings settings, ILogger<Unwrapper> logger) : IDisposable
{
    private readonly SemaphoreSlim slots = new(settings.MaxConcurrentProcesses, settings.MaxConcurrentProcesses);

    public async Task<UnwrapResult> UnwrapAsync(UnwrapRequest request, CancellationToken cancellationToken)
    {
        if (request.File.Length > settings.MaxUploadBytes)
            throw new UnwrapException(413, "The OBJ exceeds the configured upload limit.");
        if (!File.Exists(installer.ExecutablePath))
        {
            try { await installer.EnsureInstalledAsync(cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException or TimeoutException)
            {
                logger.LogWarning(exception, "Ministry of Flat binary is unavailable");
                throw new UnwrapException(503, "Ministry of Flat is not ready. Retry later.");
            }
            if (!File.Exists(installer.ExecutablePath))
                throw new UnwrapException(503, "Ministry of Flat is not ready. Retry later.");
        }
        if (!await slots.WaitAsync(0, cancellationToken))
            throw new UnwrapException(503, "All unwrap processes are busy. Retry later.");
        var baseDirectory = Path.GetFullPath(settings.TempDirectory);
        var directory = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var input = Path.Combine(directory, "input.obj");
            var output = Path.Combine(directory, "output.obj");
            await using (var source = request.File.OpenReadStream())
            await using (var target = File.Create(input))
            {
                try { await LimitedCopy.CopyAsync(source, target, settings.MaxUploadBytes, cancellationToken); }
                catch (InvalidDataException)
                {
                    throw new UnwrapException(413, "The OBJ exceeds the configured upload limit.");
                }
            }

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
                    catch (Exception exception) when (exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        // Already exited or cannot signal; the wait below still reaps the process.
                    }
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
            if (!await HasUvAsync(output, cancellationToken))
                throw new UnwrapException(422, "Ministry of Flat did not produce a mesh with UV coordinates.");
            // Hand ownership of the completed mesh to the caller (streamed to the client
            // without buffering the whole file in memory); the request directory is removed below.
            Directory.CreateDirectory(baseDirectory);
            var completed = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N") + ".obj");
            File.Move(output, completed);
            return new UnwrapResult(completed);
        }
        finally
        {
            await DeleteWithRetryAsync(directory);
            slots.Release();
        }
    }

    private static async Task<bool> HasUvAsync(string path, CancellationToken cancellationToken)
    {
        var hasVt = false;
        var hasFace = false;
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!hasVt && line.StartsWith("vt ", StringComparison.Ordinal)) hasVt = true;
            else if (!hasFace && line.StartsWith("f ", StringComparison.Ordinal) && line.Contains('/')) hasFace = true;
            if (hasVt && hasFace) return true;
        }
        return false;
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

    private async Task DeleteWithRetryAsync(string directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException exception) when (attempt < 2)
            {
                logger.LogDebug(exception, "Temporary directory {Path} is locked, retrying", directory);
            }
            catch (UnauthorizedAccessException exception) when (attempt < 2)
            {
                logger.LogDebug(exception, "Temporary directory {Path} is locked, retrying", directory);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not clean temporary directory {Path}", directory);
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(exception, "Could not clean temporary directory {Path}", directory);
                return;
            }
            await Task.Delay(100 * (attempt + 1), CancellationToken.None);
        }
    }

    public void Dispose() => slots.Dispose();
}

/// <summary>
/// A completed unwrap mesh staged on disk. The owner must dispose it (deleting the file)
/// once the response has been sent.
/// </summary>
public sealed class UnwrapResult(string contentPath) : IDisposable
{
    public string ContentPath { get; } = contentPath;

    public void Dispose()
    {
        try { File.Delete(ContentPath); }
        catch (IOException) { /* Best effort: container temp is recycled on restart. */ }
        catch (UnauthorizedAccessException) { /* Best effort: container temp is recycled on restart. */ }
    }
}

public sealed class UnwrapException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
