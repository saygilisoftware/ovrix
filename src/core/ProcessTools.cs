using System.Diagnostics;
using System.IO;
using System.Text;

namespace Ovrix.Engine;

public static class ProcessTools
{
    public static string Find(string configured, string name)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathFullyQualified(configured) && File.Exists(configured) &&
                Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return configured;
            throw new FileNotFoundException($"Invalid executable path: {name}.");
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(folder.Trim('"'))) continue;
            var path = Path.Combine(folder.Trim('"'), name + ".exe");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"{name}.exe — PATH");
    }

    public static Process Start(string exe, IEnumerable<string> arguments, bool input = false)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = input,
            StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Unable to start process.");
    }

    public static void Terminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public static async Task<(int ExitCode, string Output, string Errors)> CaptureAsync(
        string exe, IEnumerable<string> arguments, CancellationToken token)
    {
        using var child = Start(exe, arguments);
        using var stop = token.Register(() => Terminate(child));
        var output = child.StandardOutput.ReadToEndAsync(token);
        var errors = child.StandardError.ReadToEndAsync(token);
        await child.WaitForExitAsync(token);
        return (child.ExitCode, await output, await errors);
    }
}
