using System.Globalization;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Sefirah.Data.Models;
using Sefirah.Utils;
using Windows.ApplicationModel.DataTransfer;

namespace Sefirah.Platforms.Desktop.Features;

public class SftpFeature(
    ILogger<SftpFeature> logger,
    IUserSettingsService userSettingsService,
    ISessionManager sessionManager) : ISftpFeature
{
    private const string MacFuseInstallCommand = "brew install --cask macfuse";
    private const string SshfsInstallCommand = "brew install gromgit/fuse/sshfs-mac";

    /// <summary>
    /// Maps device id → mount target used for unmount (gio URI on Linux, local path on macOS).
    /// </summary>
    private readonly Dictionary<string, MountEntry> _mountedDevices = [];
    private bool _prerequisitesDialogVisible;

    public Task InitializeAsync()
    {
        sessionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        return Task.CompletedTask;
    }

    private void OnConnectionStatusChanged(object? sender, PairedDevice device)
    {
        if (device.IsConnected) return;
        Remove(device.Id);
    }

    public Task EnsurePrerequisitesAsync()
    {
        if (!OperatingSystem.IsMacOS())
            return Task.CompletedTask;

        return ShowMissingDependenciesDialogIfNeededAsync();
    }

    public async Task InitializeAsync(PairedDevice device, SftpServerInfo info)
    {
        if (!device.DeviceSettings.StorageAccess) return;

        if (string.IsNullOrEmpty(device.Address))
        {
            logger.Warn($"Cannot initialize SFTP service for {device.Name}: device has no address");
            return;
        }

        // Remount cleanly if we already have an entry for this device
        Remove(device.Id);

        logger.Info($"Initializing SFTP service for device {device.Name}, IP: {device.Address}, Port: {info.Port}");

        if (OperatingSystem.IsMacOS())
        {
            await MountMacAsync(device, info);
        }
        else
        {
            await MountLinuxAsync(device, info);
        }
    }

    private async Task MountLinuxAsync(PairedDevice device, SftpServerInfo info)
    {
        var sftpUri = $"sftp://{info.Username}@{device.Address}:{info.Port}/";

        logger.Info($"Mounting SFTP via gio for device {device.Name}");
        ProcessExecutor.ExecuteProcess("gio", $"mount -s \"{sftpUri}\"");

        var (exitCode, errorOutput) = await ExecuteProcessWithPasswordAsync(
            "gio",
            ["mount", sftpUri],
            info.Password);

        if (exitCode != 0)
        {
            logger.Error($"Failed to mount SFTP for device {device.Name}: {errorOutput}");
            return;
        }

        _mountedDevices[device.Id] = new MountEntry(MountKind.Gio, sftpUri);
        logger.Info($"Successfully mounted SFTP for device {device.Name}");
    }

    private async Task MountMacAsync(PairedDevice device, SftpServerInfo info)
    {
        if (!AreMacDependenciesInstalled(out var missing))
        {
            logger.Error($"Storage access prerequisites missing ({missing}). Install macFUSE and sshfs to enable Finder mounts.");
            await ShowMissingDependenciesDialogIfNeededAsync(missing);
            return;
        }

        var sshfsPath = FindSshfs()!;
        var remotePath = info.Paths.Count > 0 ? info.Paths[0] : "/";
        if (!remotePath.StartsWith('/'))
            remotePath = "/" + remotePath;

        var baseDirectory = userSettingsService.GeneralSettingsService.RemoteStoragePath;
        Directory.CreateDirectory(baseDirectory);

        var mountPoint = Path.Combine(baseDirectory, SanitizePathSegment(device.Name));
        Directory.CreateDirectory(mountPoint);

        // sshfs refuses non-empty mount points; clear leftover placeholders if unmounted
        if (Directory.Exists(mountPoint) && Directory.EnumerateFileSystemEntries(mountPoint).Any())
        {
            logger.Warn($"Mount point {mountPoint} is not empty; attempting unmount/cleanup first");
            TryUnmountMac(mountPoint);
            TryClearMountPointIfSafe(mountPoint);
        }

        Directory.CreateDirectory(mountPoint);

        var remoteSpec = $"{info.Username}@{device.Address}:{remotePath}";
        logger.Info($"Mounting SFTP via sshfs for device {device.Name} at {mountPoint}");

        var (exitCode, errorOutput) = await ExecuteProcessWithPasswordAsync(
            sshfsPath,
            [
                remoteSpec,
                mountPoint,
                "-p", info.Port.ToString(),
                "-o", "password_stdin",
                "-o", "StrictHostKeyChecking=no",
                "-o", "UserKnownHostsFile=/dev/null",
                "-o", $"volname={SanitizeVolName(device.Name)}",
                "-o", "kill_on_unmount",
                "-o", "reconnect",
                "-o", "defer_permissions",
                "-o", "noappledouble",
                "-o", "auto_cache",
            ],
            info.Password);

        if (exitCode != 0 || !IsPathMounted(mountPoint))
        {
            var detail = string.IsNullOrWhiteSpace(errorOutput)
                ? "sshfs exited without mounting the volume."
                : errorOutput.Trim();
            logger.Error($"Failed to mount SFTP for device {device.Name}: {detail}");
            await ShowMountFailedDialogAsync(device.Name, detail);
            TryUnmountMac(mountPoint);
            return;
        }

        _mountedDevices[device.Id] = new MountEntry(MountKind.Sshfs, mountPoint);
        logger.Info($"Successfully mounted SFTP for device {device.Name} at {mountPoint}");
        TryRevealInFinder(mountPoint);
    }

    public void RemoveAll()
    {
        foreach (var deviceId in _mountedDevices.Keys.ToList())
        {
            Remove(deviceId);
        }
    }

    public void Remove(string deviceId)
    {
        if (!_mountedDevices.TryGetValue(deviceId, out var entry))
        {
            logger.Debug($"Device {deviceId} is not mounted");
            return;
        }

        logger.Info($"Unmounting SFTP for device {deviceId}");

        switch (entry.Kind)
        {
            case MountKind.Gio:
                ProcessExecutor.ExecuteProcess("gio", $"mount -u \"{entry.Target}\"");
                break;
            case MountKind.Sshfs:
                TryUnmountMac(entry.Target);
                break;
        }

        _mountedDevices.Remove(deviceId);
    }

    private async Task ShowMissingDependenciesDialogIfNeededAsync(MacDependencyStatus? knownStatus = null)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var status = knownStatus ?? GetMacDependencyStatus();
        if (status == MacDependencyStatus.Ready)
            return;

        // Avoid stacking dialogs if mount + toggle both fire, or reconnect storms
        if (_prerequisitesDialogVisible)
            return;

        _prerequisitesDialogVisible = true;
        try
        {
            var contentKey = status is MacDependencyStatus.MissingSshfsOnly
                ? "StorageAccessDepsMissingSshfs"
                : "StorageAccessDepsMissingMacFuse";

            var content = LocalizedOr(
                contentKey,
                status is MacDependencyStatus.MissingSshfsOnly
                    ? $"To show this device in Finder, install sshfs, then reconnect:\n\n{SshfsInstallCommand}"
                    : $"To show this device in Finder, install macFUSE and sshfs, then reconnect:\n\n{MacFuseInstallCommand}\n{SshfsInstallCommand}\n\nAfter installing macFUSE, you may need to allow the system extension in System Settings and reboot.");

            var installCommands = status is MacDependencyStatus.MissingSshfsOnly
                ? SshfsInstallCommand
                : $"{MacFuseInstallCommand}\n{SshfsInstallCommand}";

            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                if (App.MainWindow.Content?.XamlRoot is null)
                    return;

                var dialog = new ContentDialog
                {
                    XamlRoot = App.MainWindow.Content.XamlRoot,
                    Title = LocalizedOr("StorageAccessDepsMissingTitle", "Additional software required"),
                    Content = content,
                    PrimaryButtonText = LocalizedOr("CopyInstallCommands", "Copy install commands"),
                    CloseButtonText = "Dismiss".GetLocalizedResource(),
                    DefaultButton = ContentDialogButton.Primary,
                };

                if (await dialog.ShowAsync() is ContentDialogResult.Primary)
                {
                    var dataPackage = new DataPackage();
                    dataPackage.SetText(installCommands);
                    Clipboard.SetContent(dataPackage);
                }
            });
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to show storage prerequisites dialog: {ex.Message}");
        }
        finally
        {
            _prerequisitesDialogVisible = false;
        }
    }

    private void TryUnmountMac(string mountPoint)
    {
        try
        {
            var psi = new ProcessStartInfo("umount")
            {
                ArgumentList = { mountPoint },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);

            if (process is { ExitCode: not 0 })
            {
                // Force unmount if a lazy umount failed (busy mount)
                ProcessExecutor.ExecuteProcess("diskutil", $"unmount force \"{mountPoint}\"");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to unmount {mountPoint}: {ex.Message}");
        }
    }

    private void TryClearMountPointIfSafe(string mountPoint)
    {
        try
        {
            if (!Directory.Exists(mountPoint) || IsPathMounted(mountPoint))
                return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(mountPoint))
            {
                try
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                    else
                        File.Delete(entry);
                }
                catch (Exception ex)
                {
                    logger.Debug($"Could not clear {entry}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to clear mount point {mountPoint}: {ex.Message}");
        }
    }

    private static bool IsPathMounted(string mountPoint)
    {
        try
        {
            var full = Path.GetFullPath(mountPoint).TrimEnd('/');
            var psi = new ProcessStartInfo("mount")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.Contains($" on {full} ", StringComparison.Ordinal)
                             || line.Contains($" on {full}(", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private static void TryRevealInFinder(string mountPoint)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { mountPoint },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
        }
    }

    private async Task ShowMountFailedDialogAsync(string deviceName, string detail)
    {
        try
        {
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                if (App.MainWindow.Content?.XamlRoot is null)
                    return;

                var template = LocalizedOr(
                    "StorageAccessMountFailed",
                    "Could not mount \"{0}\" in Finder.\n\n{1}\n\nCheck that macFUSE is allowed in System Settings → Privacy & Security, then reconnect.");
                var body = string.Format(CultureInfo.CurrentCulture, template, deviceName, detail);

                var dialog = new ContentDialog
                {
                    XamlRoot = App.MainWindow.Content.XamlRoot,
                    Title = LocalizedOr("StorageAccessMountFailedTitle", "Finder mount failed"),
                    Content = body,
                    PrimaryButtonText = LocalizedOr("MacPermissionsOpenSettings", "Open Privacy settings"),
                    CloseButtonText = "Dismiss".GetLocalizedResource(),
                    DefaultButton = ContentDialogButton.Close,
                };

                if (await dialog.ShowAsync() is ContentDialogResult.Primary)
                    Platforms.Desktop.Services.MacOsPermissionsHelper.OpenPrivacySettings();
            });
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to show mount failure dialog: {ex.Message}");
        }
    }

    private static async Task<(int ExitCode, string ErrorOutput)> ExecuteProcessWithPasswordAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string password)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi);
        if (process is null)
            return (-1, "Failed to start process");

        await process.StandardInput.WriteLineAsync(password);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        await process.WaitForExitAsync();
        var errorOutput = await process.StandardError.ReadToEndAsync();

        return (process.ExitCode, errorOutput);
    }

    private static bool AreMacDependenciesInstalled(out MacDependencyStatus status)
    {
        status = GetMacDependencyStatus();
        return status == MacDependencyStatus.Ready;
    }

    private static MacDependencyStatus GetMacDependencyStatus()
    {
        var hasMacFuse = IsMacFuseInstalled();
        var hasSshfs = FindSshfs() is not null;

        return (hasMacFuse, hasSshfs) switch
        {
            (true, true) => MacDependencyStatus.Ready,
            (false, _) => MacDependencyStatus.MissingMacFuse,
            (true, false) => MacDependencyStatus.MissingSshfsOnly,
        };
    }

    private static bool IsMacFuseInstalled() =>
        Directory.Exists("/Library/Filesystems/macfuse.fs")
        || Directory.Exists("/Library/Filesystems/osxfuse.fs");

    private static string? FindSshfs()
    {
        string[] candidates =
        [
            "/opt/homebrew/bin/sshfs",
            "/usr/local/bin/sshfs",
            "/opt/local/bin/sshfs",
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return FindOnPath("sshfs");
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var directory in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (string.IsNullOrEmpty(trimmed)) continue;

            var candidate = Path.Combine(trimmed, fileName);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static string SanitizePathSegment(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(sanitized) ? "Device" : sanitized;
    }

    private static string SanitizeVolName(string name)
    {
        // FUSE option values cannot contain commas; keep Finder label readable otherwise
        var sanitized = name.Replace(',', '_').Replace('=', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "Device" : sanitized;
    }

    private static string LocalizedOr(string key, string fallback)
    {
        var value = key.GetLocalizedResource();
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }

    private enum MountKind
    {
        Gio,
        Sshfs,
    }

    private enum MacDependencyStatus
    {
        Ready,
        MissingMacFuse,
        MissingSshfsOnly,
    }

    private readonly record struct MountEntry(MountKind Kind, string Target);
}
