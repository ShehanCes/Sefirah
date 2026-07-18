namespace Sefirah.Utils;

/// <summary>
/// Locates adb/scrcpy binaries across Windows, macOS, and Linux.
/// </summary>
public static class ExternalToolLocator
{
    public static string AdbFileName => OperatingSystem.IsWindows() ? "adb.exe" : "adb";
    public static string ScrcpyFileName => OperatingSystem.IsWindows() ? "scrcpy.exe" : "scrcpy";

    /// <summary>
    /// If the companion tool sits next to <paramref name="selectedPath"/>, invokes <paramref name="setPath"/>.
    /// </summary>
    public static void TrySetCompanionTool(string selectedPath, string companionName, Action<string> setPath)
    {
        var directory = Path.GetDirectoryName(selectedPath);
        if (string.IsNullOrEmpty(directory)) return;

        var companionPath = Path.GetFullPath(Path.Combine(directory, companionName));
        if (File.Exists(companionPath))
        {
            setPath(companionPath);
        }
    }

    public static void TrySetAdbCompanion(string scrcpyPath, Action<string> setAdbPath) =>
        TrySetCompanionTool(scrcpyPath, AdbFileName, setAdbPath);

    public static void TrySetScrcpyCompanion(string adbPath, Action<string> setScrcpyPath) =>
        TrySetCompanionTool(adbPath, ScrcpyFileName, setScrcpyPath);

    /// <summary>
    /// Returns a usable adb path: configured path if it exists, otherwise a discovered install.
    /// </summary>
    public static string ResolveAdbPath(string? configuredPath) =>
        ResolvePath(configuredPath, FindAdb);

    /// <summary>
    /// Returns a usable scrcpy path: configured path if it exists, otherwise a discovered install.
    /// </summary>
    public static string ResolveScrcpyPath(string? configuredPath) =>
        ResolvePath(configuredPath, FindScrcpy);

    public static string? FindAdb() => FindTool(AdbFileName, GetAdbCandidateDirectories());

    public static string? FindScrcpy() => FindTool(ScrcpyFileName, GetScrcpyCandidateDirectories());

    private static string ResolvePath(string? configuredPath, Func<string?> find)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        return find() ?? configuredPath ?? string.Empty;
    }

    private static string? FindTool(string fileName, IEnumerable<string> candidateDirectories)
    {
        foreach (var directory in candidateDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }

        return FindOnPath(fileName);
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var directory in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (string.IsNullOrEmpty(trimmed)) continue;

            var candidate = Path.Combine(trimmed, fileName);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static IEnumerable<string> GetAdbCandidateDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(localAppData, "Android", "Sdk", "platform-tools");
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "Library", "Android", "sdk", "platform-tools");
        yield return Path.Combine(home, "Android", "Sdk", "platform-tools");
        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
        yield return "/opt/local/bin";

        foreach (var directory in EnumerateHomebrewCaskPlatformTools())
            yield return directory;
    }

    private static IEnumerable<string> GetScrcpyCandidateDirectories()
    {
        if (OperatingSystem.IsWindows())
            yield break;

        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
        yield return "/opt/local/bin";
        yield return "/opt/homebrew/opt/scrcpy/bin";
        yield return "/usr/local/opt/scrcpy/bin";
    }

    /// <summary>
    /// Homebrew cask installs, e.g. /opt/homebrew/Caskroom/android-platform-tools/37.0.0/platform-tools
    /// </summary>
    private static IEnumerable<string> EnumerateHomebrewCaskPlatformTools()
    {
        foreach (var caskroom in new[]
        {
            "/opt/homebrew/Caskroom/android-platform-tools",
            "/usr/local/Caskroom/android-platform-tools"
        })
        {
            if (!Directory.Exists(caskroom)) continue;

            IEnumerable<string> versions;
            try
            {
                versions = Directory.EnumerateDirectories(caskroom);
            }
            catch
            {
                continue;
            }

            foreach (var versionDir in versions)
                yield return Path.Combine(versionDir, "platform-tools");
        }
    }
}
