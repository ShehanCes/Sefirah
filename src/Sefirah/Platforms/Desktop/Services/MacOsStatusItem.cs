using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// Minimal macOS menu bar status item via the Objective-C runtime.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOsStatusItem : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static readonly IntPtr SelAlloc = sel_registerName("alloc");
    private static readonly IntPtr SelInit = sel_registerName("init");
    private static readonly IntPtr SelRetain = sel_registerName("retain");
    private static readonly IntPtr SelRelease = sel_registerName("release");
    private static readonly IntPtr SelSystemStatusBar = sel_registerName("systemStatusBar");
    private static readonly IntPtr SelStatusItemWithLength = sel_registerName("statusItemWithLength:");
    private static readonly IntPtr SelSetTitle = sel_registerName("setTitle:");
    private static readonly IntPtr SelSetToolTip = sel_registerName("setToolTip:");
    private static readonly IntPtr SelSetHighlightMode = sel_registerName("setHighlightMode:");
    private static readonly IntPtr SelSetMenu = sel_registerName("setMenu:");
    private static readonly IntPtr SelAddItem = sel_registerName("addItem:");
    private static readonly IntPtr SelSetTarget = sel_registerName("setTarget:");
    private static readonly IntPtr SelSetAction = sel_registerName("setAction:");
    private static readonly IntPtr SelSetTag = sel_registerName("setTag:");
    private static readonly IntPtr SelSetEnabled = sel_registerName("setEnabled:");
    private static readonly IntPtr SelTag = sel_registerName("tag");
    private static readonly IntPtr SelSeparatorItem = sel_registerName("separatorItem");
    private static readonly IntPtr SelStringWithUTF8 = sel_registerName("stringWithUTF8String:");
    private static readonly IntPtr SelButton = sel_registerName("button");
    private static readonly IntPtr SelRemoveStatusItem = sel_registerName("removeStatusItem:");
    private static readonly IntPtr SelMenuClicked = sel_registerName("menuClicked:");
    private static readonly IntPtr SelInitWithContentsOfFile = sel_registerName("initWithContentsOfFile:");
    private static readonly IntPtr SelSetImage = sel_registerName("setImage:");
    private static readonly IntPtr SelSetTemplate = sel_registerName("setTemplate:");
    private static readonly IntPtr SelSetSize = sel_registerName("setSize:");

    private const double VariableStatusItemLength = -1.0;

    private static readonly object ClassGate = new();
    private static IntPtr sharedTargetClass;
    private static readonly List<GCHandle> AliveDelegates = [];

    private IntPtr statusItem;
    private IntPtr menu;
    private IntPtr target;
    private GCHandle actionsHandle;
    private bool disposed;

    public static MacOsStatusItem? TryCreate(
        string title,
        string tooltip,
        string? iconPath,
        (string Label, Action? Callback)[] items,
        ILogger logger)
    {
        try
        {
            dlopen(AppKit, 0);
            var instance = new MacOsStatusItem();
            instance.Build(title, tooltip, iconPath, items);
            return instance;
        }
        catch (Exception ex)
        {
            logger.Warn("Failed to create macOS status item", ex);
            return null;
        }
    }

    private void Build(string title, string tooltip, string? iconPath, (string Label, Action? Callback)[] items)
    {
        var actions = items.Select(i => i.Callback).ToArray();
        actionsHandle = GCHandle.Alloc(actions);

        var nsStatusBarClass = objc_getClass("NSStatusBar");
        var statusBar = objc_msgSend(nsStatusBarClass, SelSystemStatusBar);
        statusItem = objc_msgSend_nfloat(statusBar, SelStatusItemWithLength, VariableStatusItemLength);
        statusItem = objc_msgSend(statusItem, SelRetain);

        var tipStr = CreateNSString(tooltip);
        var button = objc_msgSend(statusItem, SelButton);

        var hasIcon = false;
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            var pathStr = CreateNSString(iconPath);
            var image = objc_msgSend_IntPtr(
                objc_msgSend(objc_getClass("NSImage"), SelAlloc),
                SelInitWithContentsOfFile,
                pathStr);
            if (image != IntPtr.Zero)
            {
                objc_msgSend_byte(image, SelSetTemplate, 1);
                // 18x18 points is a typical menu bar size
                SetImageSize(image, 18, 18);
                if (button != IntPtr.Zero)
                {
                    objc_msgSend_IntPtr(button, SelSetImage, image);
                    objc_msgSend_IntPtr(button, SelSetTitle, CreateNSString(string.Empty));
                }
                else
                {
                    objc_msgSend_IntPtr(statusItem, SelSetImage, image);
                }

                hasIcon = true;
            }
        }

        if (!hasIcon)
        {
            var titleStr = CreateNSString(title);
            if (button != IntPtr.Zero)
                objc_msgSend_IntPtr(button, SelSetTitle, titleStr);
            else
                objc_msgSend_IntPtr(statusItem, SelSetTitle, titleStr);
        }

        objc_msgSend_IntPtr(statusItem, SelSetToolTip, tipStr);
        objc_msgSend_byte(statusItem, SelSetHighlightMode, 1);

        menu = objc_msgSend(objc_msgSend(objc_getClass("NSMenu"), SelAlloc), SelInit);
        menu = objc_msgSend(menu, SelRetain);

        target = CreateTarget(actionsHandle);

        for (var i = 0; i < items.Length; i++)
        {
            if (string.IsNullOrEmpty(items[i].Label) || items[i].Callback is null)
            {
                var separator = objc_msgSend(objc_getClass("NSMenuItem"), SelSeparatorItem);
                objc_msgSend_IntPtr(menu, SelAddItem, separator);
                continue;
            }

            var label = CreateNSString(items[i].Label);
            var menuItem = objc_msgSend(objc_msgSend(objc_getClass("NSMenuItem"), SelAlloc), SelInit);
            objc_msgSend_IntPtr(menuItem, SelSetTitle, label);
            objc_msgSend_IntPtr(menuItem, SelSetTarget, target);
            objc_msgSend_IntPtr(menuItem, SelSetAction, SelMenuClicked);
            objc_msgSend_nint(menuItem, SelSetTag, i);
            objc_msgSend_byte(menuItem, SelSetEnabled, 1);
            objc_msgSend_IntPtr(menu, SelAddItem, menuItem);
        }

        objc_msgSend_IntPtr(statusItem, SelSetMenu, menu);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        try
        {
            if (statusItem != IntPtr.Zero)
            {
                var statusBar = objc_msgSend(objc_getClass("NSStatusBar"), SelSystemStatusBar);
                objc_msgSend_IntPtr(statusBar, SelRemoveStatusItem, statusItem);
                objc_msgSend(statusItem, SelRelease);
                statusItem = IntPtr.Zero;
            }

            if (menu != IntPtr.Zero)
            {
                objc_msgSend(menu, SelRelease);
                menu = IntPtr.Zero;
            }

            if (target != IntPtr.Zero)
            {
                objc_msgSend(target, SelRelease);
                target = IntPtr.Zero;
            }
        }
        finally
        {
            if (actionsHandle.IsAllocated)
                actionsHandle.Free();
        }
    }

    private static void SetImageSize(IntPtr image, double width, double height)
    {
        var size = new CGSize { Width = width, Height = height };
        objc_msgSend_CGSize(image, SelSetSize, size);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
    }

    private static IntPtr CreateNSString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return objc_msgSend_IntPtr(objc_getClass("NSString"), SelStringWithUTF8, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static IntPtr CreateTarget(GCHandle actionsHandle)
    {
        EnsureTargetClass();
        var instance = objc_msgSend(objc_msgSend(sharedTargetClass, SelAlloc), SelInit);
        // Stash GCHandle in associated object via isa... we use a static map keyed by instance pointer instead.
        TargetMap[instance] = actionsHandle;
        return objc_msgSend(instance, SelRetain);
    }

    private static readonly Dictionary<IntPtr, GCHandle> TargetMap = [];

    private static void EnsureTargetClass()
    {
        lock (ClassGate)
        {
            if (sharedTargetClass != IntPtr.Zero)
                return;

            var name = "SefirahMacOsStatusItemTarget";
            sharedTargetClass = objc_allocateClassPair(objc_getClass("NSObject"), name, 0);
            if (sharedTargetClass == IntPtr.Zero)
            {
                // Already registered from a previous run in-process.
                sharedTargetClass = objc_getClass(name);
                if (sharedTargetClass == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to create ObjC target class");
                return;
            }

            MenuActionCallback callback = (self, _, sender) =>
            {
                if (!TargetMap.TryGetValue(self, out var handle) || !handle.IsAllocated)
                    return;
                if (handle.Target is not Action?[] actions)
                    return;

                var tag = (int)objc_msgSend_ret_nint(sender, SelTag);
                if (tag < 0 || tag >= actions.Length)
                    return;

                try { actions[tag]?.Invoke(); }
                catch { /* keep AppKit alive */ }
            };

            var imp = Marshal.GetFunctionPointerForDelegate(callback);
            AliveDelegates.Add(GCHandle.Alloc(callback));
            if (!class_addMethod(sharedTargetClass, SelMenuClicked, imp, "v@:@"))
                throw new InvalidOperationException("class_addMethod failed");

            objc_registerClassPair(sharedTargetClass);
        }
    }

    private delegate void MenuActionCallback(IntPtr self, IntPtr cmd, IntPtr sender);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);

    [DllImport(ObjC)]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(ObjC)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_nfloat(IntPtr receiver, IntPtr selector, double arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_byte(IntPtr receiver, IntPtr selector, byte arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_nint(IntPtr receiver, IntPtr selector, nint arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_ret_nint(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_CGSize(IntPtr receiver, IntPtr selector, CGSize arg1);
}
