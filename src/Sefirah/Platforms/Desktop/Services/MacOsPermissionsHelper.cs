using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// First-run guidance for macOS privacy permissions Sefirah needs.
/// </summary>
internal static class MacOsPermissionsHelper
{
    private const string SeenKey = "HasSeenMacPermissionsTips";

    public static async Task ShowTipsIfNeededAsync()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        if (ApplicationData.Current.LocalSettings.Values[SeenKey] is true)
            return;

        try
        {
            // Warm notification path (UserNotifications when packaged, otherwise no-op).
            MacOsNative.EnsureLoaded();
            if (MacOsNative.NotificationsAvailable)
                MacOsNative.RequestAuthorization();

            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                if (App.MainWindow.Content?.XamlRoot is null)
                    return;

                var dialog = new ContentDialog
                {
                    XamlRoot = App.MainWindow.Content.XamlRoot,
                    Title = LocalizedOr("MacPermissionsTitle", "macOS permissions"),
                    Content = LocalizedOr(
                        "MacPermissionsBody",
                        "For the best experience, allow these when macOS asks (or enable them in System Settings → Privacy & Security):\n\n" +
                        "• Local Network — find and connect to your phone\n" +
                        "• Notifications — show Android alerts on your Mac\n" +
                        "• Automation (System Events) — lock / sleep / login item actions\n\n" +
                        "Storage in Finder also needs macFUSE + sshfs (you’ll be prompted if missing)."),
                    PrimaryButtonText = LocalizedOr("MacPermissionsOpenSettings", "Open Privacy settings"),
                    SecondaryButtonText = LocalizedOr("MacPermissionsContinue", "Continue"),
                    DefaultButton = ContentDialogButton.Secondary,
                };

                var result = await dialog.ShowAsync();
                ApplicationData.Current.LocalSettings.Values[SeenKey] = true;

                if (result is ContentDialogResult.Primary)
                    OpenPrivacySettings();
            });
        }
        catch (Exception)
        {
            // Never block startup on the tips dialog.
            ApplicationData.Current.LocalSettings.Values[SeenKey] = true;
        }
    }

    public static void OpenPrivacySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList =
                {
                    "x-apple.systempreferences:com.apple.preference.security?Privacy"
                },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { "/System/Library/PreferencePanes/Security.prefPane" },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch
            {
            }
        }
    }

    private static string LocalizedOr(string key, string fallback)
    {
        var value = key.GetLocalizedResource();
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }
}
