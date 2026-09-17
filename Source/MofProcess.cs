using System.Diagnostics;

namespace Mof.Http;

public static class MofProcess
{
    public static ProcessStartInfo CreateStartInfo(MofSettings settings, string executable,
        string directory, UnwrapRequest request)
    {
        var start = new ProcessStartInfo(settings.WineExecutable ?? executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (settings.WineExecutable is not null) start.ArgumentList.Add(executable);
        // Relative mesh paths work both natively and under Wine, without depending on
        // a particular Wine drive mapping for the Linux temporary directory.
        foreach (var argument in request.Arguments("input.obj", "output.obj"))
            start.ArgumentList.Add(argument);
        return start;
    }
}
