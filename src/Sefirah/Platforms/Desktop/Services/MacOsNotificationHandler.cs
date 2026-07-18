using Sefirah.Data.Models;
using Sefirah.Utils;

namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// macOS notification handler using UserNotifications (via libsefirah_macos),
/// with osascript fallback when the native library is unavailable.
/// </summary>
public sealed class MacOsNotificationHandler(
    ILogger<MacOsNotificationHandler> logger,
    IDeviceManager deviceManager) : IPlatformNotificationHandler, IDisposable
{
    private const string RemoteCategoryPrefix = "sefirah.remote.";
    private const string ClipboardCategory = "sefirah.clipboard";
    private const string CallCategory = "sefirah.call";

    private readonly HashSet<string> activeTags = new(StringComparer.Ordinal);
    private bool disposed;

    public Task ShowRemoteNotification(NotificationInfo message, string deviceId)
    {
        var title = message.AppName ?? message.Title ?? "Sefirah";
        var body = BuildBody(message.Title, message.Text);
        var id = string.IsNullOrEmpty(message.NotificationKey)
            ? Guid.NewGuid().ToString("N")
            : message.NotificationKey;

        var actions = message.Actions
            .Where(a => a is not null && !string.IsNullOrEmpty(a.Label))
            .Select(a => (
                Id: $"action_{a!.ActionIndex}",
                Title: a.Label!,
                Foreground: true,
                Destructive: false))
            .Take(4)
            .ToArray();

        string? category = null;
        if (actions.Length > 0 && MacOsNative.NotificationsAvailable)
        {
            category = RemoteCategoryPrefix + Math.Abs(id.GetHashCode()).ToString("x");
            MacOsNative.RegisterCategory(category, actions);
        }

        var userInfo = new Dictionary<string, string>
        {
            ["type"] = "remote",
            ["deviceId"] = deviceId,
            ["notificationKey"] = message.NotificationKey ?? id
        };

        Show(id, title, body, category, userInfo);
        return Task.CompletedTask;
    }

    public void ShowClipboardNotification(string title, string text, string? actionLabel = null, string? actionData = null)
    {
        var id = $"clipboard_{Guid.NewGuid():N}";
        string? category = null;
        Dictionary<string, string>? userInfo = new() { ["type"] = "clipboard" };

        if (!string.IsNullOrEmpty(actionLabel) && !string.IsNullOrEmpty(actionData) && MacOsNative.NotificationsAvailable)
        {
            category = ClipboardCategory;
            MacOsNative.RegisterCategory(category,
            [
                (Id: "clipboard_action", Title: actionLabel!, Foreground: true, Destructive: false)
            ]);
            userInfo["uri"] = actionData!;
        }
        else if (!string.IsNullOrEmpty(actionLabel) && !string.IsNullOrEmpty(actionData))
        {
            text = string.IsNullOrWhiteSpace(text)
                ? $"{actionLabel}: {actionData}"
                : $"{text}\n{actionLabel}: {actionData}";
        }

        Show(id, title, text, category, userInfo);
    }

    public Task ShowBatteryNotification(string title, string text, string tag)
    {
        Show(tag, title, text, category: null, userInfo: new() { ["type"] = "battery" });
        return Task.CompletedTask;
    }

    public void ShowCompletedFileTransferNotification(string subtitle, string transferId, string? filePath = null, string? folderPath = null)
    {
        var reveal = folderPath ?? filePath;
        string? category = null;
        var userInfo = new Dictionary<string, string> { ["type"] = "fileComplete" };
        if (!string.IsNullOrEmpty(reveal) && MacOsNative.NotificationsAvailable)
        {
            category = "sefirah.file";
            MacOsNative.RegisterCategory(category,
            [
                (Id: "reveal", Title: "Show in Finder", Foreground: true, Destructive: false)
            ]);
            userInfo["path"] = reveal!;
        }

        Show(
            transferId,
            "FileTransferNotification.Completed".GetLocalizedResource(),
            subtitle,
            category,
            userInfo);
    }

    public void ShowFileTransferNotification(
        string notificationTitle,
        string progressTitle,
        string status,
        string transferId,
        uint notificationSequence,
        double progress)
    {
        // Avoid spamming Notification Center with progress ticks.
        if (progress is > 0 and < 100 && notificationSequence % 10 != 0)
            return;

        Show(
            transferId,
            notificationTitle,
            $"{progressTitle} — {status} ({progress:F0}%)",
            category: null,
            userInfo: new() { ["type"] = "fileProgress" });
    }

    public Task ShowCallNotification(string title, string text, string tag, CallState callState, Uri? icon = null)
    {
        Show(tag, title, text, category: null, userInfo: new() { ["type"] = "call" });
        return Task.CompletedTask;
    }

    public Task ShowCallNotification(string callId, string transportDeviceId, string title, string displayName, Uri? icon = null)
    {
        // Desktop PhoneLine is NotSupported; still surface the toast for awareness.
        if (MacOsNative.NotificationsAvailable)
        {
            MacOsNative.RegisterCategory(CallCategory,
            [
                (Id: "accept", Title: "ConnectionRequestAcceptButton".GetLocalizedResource(), Foreground: true, Destructive: false),
                (Id: "decline", Title: "ConnectionRequestRejectButton".GetLocalizedResource(), Foreground: true, Destructive: true)
            ]);
        }

        Show(callId, title, displayName, CallCategory, new Dictionary<string, string>
        {
            ["type"] = "incomingCall",
            ["callId"] = callId,
            ["transportDeviceId"] = transportDeviceId
        });
        return Task.CompletedTask;
    }

    public Task RegisterForNotifications()
    {
        MacOsNative.EnsureLoaded();
        if (MacOsNative.NotificationsAvailable)
        {
            MacOsNative.NotificationActionInvoked -= OnNotificationAction;
            MacOsNative.NotificationActionInvoked += OnNotificationAction;
            MacOsNative.RequestAuthorization();
            logger.Info("macOS notification handler ready (UserNotifications)");
        }
        else
        {
            logger.Info("macOS notification handler ready (osascript fallback; package as .app for action buttons)");
        }

        return Task.CompletedTask;
    }

    public Task RemoveNotificationByTag(string? notificationKey)
    {
        if (string.IsNullOrEmpty(notificationKey))
            return Task.CompletedTask;

        activeTags.Remove(notificationKey);
        if (MacOsNative.NotificationsAvailable)
            MacOsNative.RemoveNotification(notificationKey);
        return Task.CompletedTask;
    }

    public Task RemoveNotificationsByGroup(string? groupKey) => Task.CompletedTask;

    public Task RemoveNotificationsByTagAndGroup(string? tag, string? groupKey) => RemoveNotificationByTag(tag);

    public Task ClearAllNotifications()
    {
        activeTags.Clear();
        if (MacOsNative.NotificationsAvailable)
            MacOsNative.ClearAllNotifications();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        MacOsNative.NotificationActionInvoked -= OnNotificationAction;
    }

    private void OnNotificationAction(string notificationId, string actionId, Dictionary<string, string> userInfo)
    {
        try
        {
            if (actionId is "dismiss")
                return;

            userInfo.TryGetValue("type", out var type);
            switch (type)
            {
                case "remote" when actionId.StartsWith("action_", StringComparison.Ordinal):
                    HandleRemoteAction(userInfo, actionId);
                    break;
                case "clipboard" when actionId == "clipboard_action":
                    HandleClipboardAction(userInfo);
                    break;
                case "fileComplete" when actionId == "reveal":
                    HandleRevealAction(userInfo);
                    break;
                case "incomingCall":
                    logger.Info($"Incoming call action '{actionId}' received (PhoneLine unsupported on Desktop)");
                    break;
                default:
                    if (actionId == "default")
                        App.MainWindow.DispatcherQueue.TryEnqueue(App.ShowMainWindow);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.Error("Failed to handle macOS notification action", ex);
        }
    }

    private void HandleRemoteAction(Dictionary<string, string> userInfo, string actionId)
    {
        if (!userInfo.TryGetValue("deviceId", out var deviceId) ||
            !userInfo.TryGetValue("notificationKey", out var notificationKey))
            return;

        if (!int.TryParse(actionId.AsSpan("action_".Length), out var actionIndex))
            return;

        var device = deviceManager.FindDeviceById(deviceId);
        if (device is null)
            return;

        NotificationActionUtils.ProcessClickAction(device, notificationKey, actionIndex);
    }

    private void HandleClipboardAction(Dictionary<string, string> userInfo)
    {
        if (!userInfo.TryGetValue("uri", out var uriText) ||
            !Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "open",
            ArgumentList = { uri.ToString() },
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static void HandleRevealAction(Dictionary<string, string> userInfo)
    {
        if (!userInfo.TryGetValue("path", out var path) || string.IsNullOrEmpty(path))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "open",
            ArgumentList = { "-R", path },
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private void Show(string id, string title, string body, string? category, Dictionary<string, string>? userInfo)
    {
        activeTags.Add(id);

        if (MacOsNative.NotificationsAvailable)
        {
            MacOsNative.ShowNotification(id, title, body, category, userInfo);
            return;
        }

        ShowOsascriptFallback(title, body);
    }

    private void ShowOsascriptFallback(string title, string body)
    {
        try
        {
            var script = $"display notification {QuoteAppleScript(body)} with title {QuoteAppleScript(title)}";
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
            process?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            logger.Error("Failed to show macOS notification", ex);
        }
    }

    private static string BuildBody(string? title, string? text)
    {
        if (string.IsNullOrWhiteSpace(title))
            return text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return title;
        if (string.Equals(title, text, StringComparison.Ordinal))
            return text!;
        return $"{title}\n{text}";
    }

    private static string QuoteAppleScript(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
