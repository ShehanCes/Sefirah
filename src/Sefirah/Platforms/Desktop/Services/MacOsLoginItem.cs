namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// Registers/unregisters Sefirah as a Login Item via System Events (works for .app and raw binaries).
/// </summary>
internal static class MacOsLoginItem
{
    public static void SetEnabled(bool enable)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var path = ResolveAppPath();
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            // Remove any existing entry first to avoid duplicates.
            RunOsascript(
                $"tell application \"System Events\" to delete (every login item whose path is {Quote(path)})");

            if (enable)
            {
                RunOsascript(
                    $"tell application \"System Events\" to make login item at end with properties {{path:{Quote(path)}, hidden:false}}");
            }
        }
        catch
        {
            // Accessibility / Automation permission may be required; ignore failures.
        }
    }

    private static string ResolveAppPath()
    {
        // Prefer the .app bundle when packaged.
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var current = new DirectoryInfo(baseDir);
        while (current is not null)
        {
            if (current.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return current.FullName;
            current = current.Parent;
        }

        // Fallback: the running executable (dotnet host or apphost).
        return Environment.ProcessPath ?? string.Empty;
    }

    private static void RunOsascript(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "osascript",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        using var process = Process.Start(startInfo);
        process?.WaitForExit(5000);
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
