using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UniversalMeetingRecorder.App.Platform;

/// <summary>Немного Win32 для служебных окон (оверлеи, рамка): стили и позиционирование в пикселях.</summary>
internal static class Win32
{
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExLayered = 0x00080000;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExTopmost = 0x00000008;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    public static void AddExStyle(Window w, int flags)
    {
        var hwnd = Handle(w);
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | (long)(uint)flags));
    }

    /// <summary>Поставить окно поверх всех в физических пикселях (не DIP) — нужно для мониторов с разным масштабом.</summary>
    public static void PlaceTopmost(Window w, int x, int y, int width, int height) =>
        SetWindowPos(Handle(w), HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);

    /// <summary>Поднять поверх всех без изменения размера/позиции.</summary>
    public static void BringTopmost(Window w) =>
        SetWindowPos(Handle(w), HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow);
}
