using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// P/Invoke surface for <c>libsefirah_macos.dylib</c> (UserNotifications, NSWindow, Core Audio).
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class MacOsNative
{
    private const string LibraryName = "sefirah_macos";

    private static readonly Lock InitGate = new();
    private static bool initialized;
    private static bool available;
    private static bool notificationsAvailable;
    private static ActionCallback? actionCallbackKeepAlive;

    /// <summary>Native library loaded (window hide/show works).</summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return available;
        }
    }

    /// <summary>UserNotifications API ready (requires a packaged .app).</summary>
    public static bool NotificationsAvailable
    {
        get
        {
            EnsureLoaded();
            return notificationsAvailable;
        }
    }

    public static event Action<string, string, Dictionary<string, string>>? NotificationActionInvoked;

    public static void EnsureLoaded()
    {
        if (initialized)
            return;

        lock (InitGate)
        {
            if (initialized)
                return;
            initialized = true;

            if (!OperatingSystem.IsMacOS())
                return;

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(MacOsNative).Assembly, Resolve);
                sefirah_notify_initialize();
                actionCallbackKeepAlive = OnNativeAction;
                sefirah_notify_set_action_callback(actionCallbackKeepAlive);
                available = true;
                notificationsAvailable = sefirah_notify_is_available() != 0;
            }
            catch
            {
                available = false;
                notificationsAvailable = false;
            }
        }
    }

    public static void RequestAuthorization()
    {
        if (!NotificationsAvailable)
            return;
        sefirah_notify_request_authorization();
    }

    public static void RegisterCategory(string categoryId, IEnumerable<(string Id, string Title, bool Foreground, bool Destructive)> actions)
    {
        if (!NotificationsAvailable)
            return;

        var payload = actions.Select(a => new Dictionary<string, object>
        {
            ["id"] = a.Id,
            ["title"] = a.Title,
            ["foreground"] = a.Foreground,
            ["destructive"] = a.Destructive
        }).ToArray();

        sefirah_notify_register_category(categoryId, JsonSerializer.Serialize(payload));
        // Allow async category registration to settle.
        Thread.Sleep(50);
    }

    public static void ShowNotification(string identifier, string title, string body, string? categoryId, Dictionary<string, string>? userInfo)
    {
        if (!NotificationsAvailable)
            return;

        var json = userInfo is null ? "{}" : JsonSerializer.Serialize(userInfo);
        sefirah_notify_show(identifier, title, body, categoryId ?? string.Empty, json);
    }

    public static void RemoveNotification(string identifier)
    {
        if (!NotificationsAvailable || string.IsNullOrEmpty(identifier))
            return;
        sefirah_notify_remove(identifier);
    }

    public static void ClearAllNotifications()
    {
        if (!NotificationsAvailable)
            return;
        sefirah_notify_clear_all();
    }

    public static void HideWindows()
    {
        if (!IsAvailable)
            return;
        sefirah_window_hide();
    }

    public static void ShowWindows()
    {
        if (!IsAvailable)
            return;
        sefirah_window_show();
    }

    public static bool IsAnyWindowVisible()
    {
        if (!IsAvailable)
            return true;
        return sefirah_window_is_visible() != 0;
    }

    /// <summary>Friendly name of the current default output device, or null if unavailable.</summary>
    public static string? GetDefaultOutputDeviceName()
    {
        EnsureLoaded();
        if (!available)
            return null;

        var buffer = new byte[256];
        if (sefirah_audio_get_default_output_name(buffer, buffer.Length) == 0)
            return null;

        var nullIndex = Array.IndexOf(buffer, (byte)0);
        var length = nullIndex >= 0 ? nullIndex : buffer.Length;
        if (length == 0)
            return null;

        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("sefirah_macos", StringComparison.Ordinal))
            return IntPtr.Zero;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "libsefirah_macos.dylib"),
            Path.Combine(baseDir, "runtimes", "osx-arm64", "native", "libsefirah_macos.dylib"),
            Path.Combine(baseDir, "runtimes", "osx-x64", "native", "libsefirah_macos.dylib"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "native", "macos", "libsefirah_macos.dylib"),
        };

        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full) && NativeLibrary.TryLoad(full, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ActionCallback(IntPtr notificationId, IntPtr actionId, IntPtr userInfoJson);

    private static void OnNativeAction(IntPtr notificationIdPtr, IntPtr actionIdPtr, IntPtr userInfoJsonPtr)
    {
        try
        {
            var notificationId = Marshal.PtrToStringUTF8(notificationIdPtr) ?? string.Empty;
            var actionId = Marshal.PtrToStringUTF8(actionIdPtr) ?? string.Empty;
            var json = Marshal.PtrToStringUTF8(userInfoJsonPtr) ?? "{}";
            Dictionary<string, string> userInfo;
            try
            {
                userInfo = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
            catch
            {
                userInfo = new();
            }

            NotificationActionInvoked?.Invoke(notificationId, actionId, userInfo);
        }
        catch
        {
            // Never throw into native.
        }
    }

    [LibraryImport(LibraryName)]
    private static partial void sefirah_notify_initialize();

    [LibraryImport(LibraryName)]
    private static partial int sefirah_notify_is_available();

    [LibraryImport(LibraryName)]
    private static partial void sefirah_notify_set_action_callback(ActionCallback callback);

    [LibraryImport(LibraryName)]
    private static partial void sefirah_notify_request_authorization();

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void sefirah_notify_register_category(string categoryId, string actionsJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void sefirah_notify_show(string identifier, string title, string body, string categoryId, string userInfoJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void sefirah_notify_remove(string identifier);

    [LibraryImport(LibraryName)]
    private static partial void sefirah_notify_clear_all();

    [LibraryImport(LibraryName)]
    private static partial void sefirah_window_hide();

    [LibraryImport(LibraryName)]
    private static partial void sefirah_window_show();

    [LibraryImport(LibraryName)]
    private static partial int sefirah_window_is_visible();

    [LibraryImport(LibraryName)]
    private static partial int sefirah_audio_get_default_output_name(byte[] buffer, int bufferSize);
}
