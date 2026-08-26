using System.Runtime.InteropServices;

namespace Telekinesis.MacOS;

/// <summary>
/// Accessibility API (AXUIElement) and the CoreGraphics window list, via P/Invoke into the
/// ApplicationServices umbrella framework. AXError 0 == success.
/// </summary>
internal static class Ax
{
    private const string App = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    public const int Success = 0;               // kAXErrorSuccess
    public const int NoValue = -25212;          // kAXErrorNoValue
    public const int AttributeUnsupported = -25205;
    public const int InvalidUIElement = -25202; // stale/gone

    // ---- AXUIElement ----
    [DllImport(App)] public static extern IntPtr AXUIElementCreateSystemWide();
    [DllImport(App)] public static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(App)] public static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
    [DllImport(App)] public static extern int AXUIElementCopyActionNames(IntPtr element, out IntPtr names);
    [DllImport(App)] public static extern int AXUIElementPerformAction(IntPtr element, IntPtr action);
    [DllImport(App)] public static extern int AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);
    [DllImport(App)] public static extern int AXUIElementGetPid(IntPtr element, out int pid);
    [DllImport(App)] public static extern nint AXUIElementGetTypeID();

    // ---- AXValue (packed CGPoint/CGSize) ----
    [DllImport(App)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AXValueGetValue(IntPtr value, uint type, out CGPoint outValue);
    [DllImport(App)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AXValueGetValue(IntPtr value, uint type, out CGSize outValue);
    public const uint kAXValueCGPointType = 1;
    public const uint kAXValueCGSizeType = 2;

    // ---- Trust / permission ----
    [DllImport(App)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AXIsProcessTrusted();

    // ---- CoreGraphics window list (for enumerating apps with windows) ----
    [DllImport(App)] public static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    public const uint kCGWindowListOptionOnScreenOnly = 1;
    public const uint kCGWindowListExcludeDesktopElements = 16;

    // ---- CGEvent (input injection fallback) ----
    [DllImport(App)] public static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CGPoint pos, uint mouseButton);
    [DllImport(App)] public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort keyCode, [MarshalAs(UnmanagedType.I1)] bool keyDown);
    [DllImport(App)] public static extern void CGEventKeyboardSetUnicodeString(IntPtr @event, nint length, ushort[] unicodeString);
    [DllImport(App)] public static extern void CGEventPost(uint tap, IntPtr @event);
    [DllImport(App)] public static extern void CGEventSetFlags(IntPtr @event, ulong flags);
    public const uint kCGHIDEventTap = 0;
    public const uint kCGEventLeftMouseDown = 1, kCGEventLeftMouseUp = 2,
        kCGEventRightMouseDown = 3, kCGEventRightMouseUp = 4,
        kCGEventOtherMouseDown = 25, kCGEventOtherMouseUp = 26;
    public const uint kCGMouseButtonLeft = 0, kCGMouseButtonRight = 1, kCGMouseButtonCenter = 2;

    // ---- Attribute / action / role name strings (AX* constants are just these strings) ----
    public const string kAXRoleAttribute = "AXRole";
    public const string kAXSubroleAttribute = "AXSubrole";
    public const string kAXTitleAttribute = "AXTitle";
    public const string kAXDescriptionAttribute = "AXDescription";
    public const string kAXValueAttribute = "AXValue";
    public const string kAXChildrenAttribute = "AXChildren";
    public const string kAXWindowsAttribute = "AXWindows";
    public const string kAXPositionAttribute = "AXPosition";
    public const string kAXSizeAttribute = "AXSize";
    public const string kAXEnabledAttribute = "AXEnabled";
    public const string kAXFocusedAttribute = "AXFocused";
    public const string kAXHiddenAttribute = "AXHidden";
    public const string kAXSelectedAttribute = "AXSelected";
    public const string kAXExpandedAttribute = "AXExpanded";
    public const string kAXFocusedUIElementAttribute = "AXFocusedUIElement";
    public const string kAXFocusedApplicationAttribute = "AXFocusedApplication";
    public const string kAXPressAction = "AXPress";

    // CG window dictionary keys (their string values equal their names).
    public const string kCGWindowOwnerPID = "kCGWindowOwnerPID";
    public const string kCGWindowOwnerName = "kCGWindowOwnerName";
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint { public double X; public double Y; public CGPoint(double x, double y) { X = x; Y = y; } }

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize { public double Width; public double Height; }
