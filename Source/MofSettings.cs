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
    /// <summary>Optional SHA-256 (hex) pinned for the UnWrapConsole3.exe binary. Empty disables verification.</summary>
    public string? ExpectedSha256 { get; set; }
    /// <summary>Optional shared password protecting the UI and API. Null or empty disables authentication.</summary>
    public string? ApiPassword { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BinaryDirectory) || string.IsNullOrWhiteSpace(TempDirectory)
            || (WineExecutable is not null && string.IsNullOrWhiteSpace(WineExecutable))
            || (OperatingSystem.IsLinux() && WineExecutable is null)
            || (!string.IsNullOrEmpty(ApiPassword) && (string.IsNullOrWhiteSpace(ApiPassword) || ApiPassword.Length > 256))
            || !Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !double.IsFinite(TimeoutSeconds) || TimeoutSeconds <= 0 || TimeoutSeconds > 86400
            || MaxUploadBytes <= 0 || MaxUploadBytes > int.MaxValue - 65536
            || MaxOutputBytes <= 0 || MaxOutputBytes > int.MaxValue || MaxConcurrentProcesses < 1 || MaxConcurrentProcesses > 32)
            throw new InvalidOperationException("Invalid MinistryOfFlat settings: use HTTPS, nonempty directories, positive limits, a timeout of at most 86400 seconds, at most 32 concurrent processes, and a non-blank API password of at most 256 characters.");
        if (!string.IsNullOrEmpty(ExpectedSha256)
            && (ExpectedSha256.Length != 64 || !ExpectedSha256.All(char.IsAsciiHexDigit)))
            throw new InvalidOperationException("Invalid MinistryOfFlat settings: ExpectedSha256 must be 64 hexadecimal characters.");
    }
}
