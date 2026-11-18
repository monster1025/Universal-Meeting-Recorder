using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UniversalMeetingRecorder.App.Tray;

/// <summary>
/// Иконки трея рисуются кодом (как и иконка приложения на Mac): микрофон контуром — ожидание,
/// залитый — идёт запись, красный — микрофон отдаёт тишину (или Debug-сборка, чтобы не путать
/// с установленной копией). На Windows нет template-иконок, поэтому цвет подбирается под тему
/// панели задач (SystemUsesLightTheme).
/// </summary>
internal static class TrayIconRenderer
{
    public enum Kind
    {
        Idle,
        Recording,
    }

    public static Icon Render(Kind kind, bool red)
    {
        var size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        if (size < 16) size = 16;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var color = red ? Color.FromArgb(0xE5, 0x48, 0x4D) : TaskbarIsLight() ? Color.FromArgb(0x20, 0x20, 0x20) : Color.White;
            var s = size / 16f;
            using var pen = new Pen(color, 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var brush = new SolidBrush(color);

            // Капсула микрофона.
            var body = new RectangleF(5.5f * s, 1.5f * s, 5f * s, 8.5f * s);
            using (var path = Capsule(body))
            {
                if (kind == Kind.Recording) g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
            // Держатель — полукруг под капсулой, стойка и основание.
            g.DrawArc(pen, 3.5f * s, 4.5f * s, 9f * s, 8f * s, 0, 180);
            g.DrawLine(pen, 8f * s, 12.5f * s, 8f * s, 14.5f * s);
            g.DrawLine(pen, 5.5f * s, 14.5f * s, 10.5f * s, 14.5f * s);
        }
        var hIcon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath Capsule(RectangleF r)
    {
        var path = new GraphicsPath();
        var d = r.Width;
        path.AddArc(r.X, r.Y, d, d, 180, 180);
        path.AddLine(r.Right, r.Y + d / 2, r.Right, r.Bottom - d / 2);
        path.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
        path.CloseFigure();
        return path;
    }

    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
