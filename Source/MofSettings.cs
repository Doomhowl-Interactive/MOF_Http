namespace Mof.Http;

public sealed class MofSettings
{
    public string BinaryDirectory { get; set; } = "MOFBinary";
    public string? WineExecutable { get; set; } = OperatingSystem.IsLinux() ? "wine" : null;
    public string DownloadUrl { get; set; } = "https://www.quelsolaar.com/MinistryOfFlat_Release.zip";
    public double TimeoutSeconds { get; set; } = 120;
    public long MaxUploadBytes { get; set; } = 100 * 1024 * 1024;
    public long MaxOutputBytes { get; set; } = 200 * 1024 * 1024;
    public int MaxConcurrentProcesses { get; set; } = 2;
    public string TempDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "Mof.Http");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BinaryDirectory) || string.IsNullOrWhiteSpace(TempDirectory)
            || (WineExecutable is not null && string.IsNullOrWhiteSpace(WineExecutable))
            || (OperatingSystem.IsLinux() && WineExecutable is null)
            || !Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !double.IsFinite(TimeoutSeconds) || TimeoutSeconds <= 0 || TimeoutSeconds > 86400
            || MaxUploadBytes <= 0 || MaxUploadBytes > int.MaxValue - 65536
            || MaxOutputBytes <= 0 || MaxOutputBytes > int.MaxValue || MaxConcurrentProcesses < 1)
            throw new InvalidOperationException("Invalid MinistryOfFlat settings: use HTTPS, nonempty directories, positive limits, and a timeout of at most 86400 seconds.");
    }
}
