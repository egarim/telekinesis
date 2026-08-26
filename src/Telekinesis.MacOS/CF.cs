using System.Runtime.InteropServices;

namespace Telekinesis.MacOS;

/// <summary>
/// Minimal CoreFoundation interop: string/array/number/dictionary marshalling and
/// reference-count lifetime. CoreFoundation ownership rule: anything from a Create/Copy
/// call is owned by us and must be CFRelease'd; values obtained from Get* are borrowed.
/// </summary>
internal static class CF
{
    private const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint UTF8 = 0x08000100; // kCFStringEncodingUTF8

    [DllImport(Lib)] internal static extern void CFRelease(IntPtr cf);
    [DllImport(Lib)] internal static extern IntPtr CFRetain(IntPtr cf);
    [DllImport(Lib)] internal static extern nint CFGetTypeID(IntPtr cf);

    [DllImport(Lib)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, byte[] cStr, uint encoding);
    [DllImport(Lib)] private static extern nint CFStringGetLength(IntPtr theString);
    [DllImport(Lib)] private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(Lib)] internal static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(Lib)] internal static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint idx);

    [DllImport(Lib)] internal static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    [DllImport(Lib)] internal static extern nint CFStringGetTypeID();
    [DllImport(Lib)] internal static extern nint CFArrayGetTypeID();

    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(IntPtr number, nint theType, out long value);
    private const nint kCFNumberSInt64Type = 4;

    /// <summary>Creates an owned CFString from a managed string. Caller must CFRelease.</summary>
    public static IntPtr CFStr(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + '\0');
        return CFStringCreateWithCString(IntPtr.Zero, bytes, UTF8);
    }

    /// <summary>Reads a borrowed CFStringRef to a managed string (null if not a string / empty).</summary>
    public static string? ToString(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero) return null;
        var len = CFStringGetLength(cfString);
        if (len == 0) return string.Empty;
        var max = CFStringGetMaximumSizeForEncoding(len, UTF8) + 1;
        var buffer = new byte[max];
        if (!CFStringGetCString(cfString, buffer, max, UTF8)) return null;
        var n = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, n < 0 ? buffer.Length : n);
    }

    /// <summary>Reads a borrowed CFNumberRef as a long.</summary>
    public static long ToLong(IntPtr cfNumber) =>
        cfNumber != IntPtr.Zero && CFNumberGetValue(cfNumber, kCFNumberSInt64Type, out var v) ? v : 0;

    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFBooleanGetValue(IntPtr boolean);
    public static bool ToBool(IntPtr cfBoolean) => cfBoolean != IntPtr.Zero && CFBooleanGetValue(cfBoolean);

    [DllImport(Lib)] private static extern IntPtr CFNumberCreate(IntPtr alloc, nint theType, ref double value);
    private const nint kCFNumberDoubleType = 13;
    /// <summary>Creates an owned CFNumber(double). Caller must CFRelease.</summary>
    public static IntPtr Number(double value) => CFNumberCreate(IntPtr.Zero, kCFNumberDoubleType, ref value);

    public static bool IsString(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == CFStringGetTypeID();
    public static bool IsArray(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == CFArrayGetTypeID();

    public static void ReleaseIf(IntPtr cf) { if (cf != IntPtr.Zero) CFRelease(cf); }
}
