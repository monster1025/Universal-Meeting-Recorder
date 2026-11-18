using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UniversalMeetingRecorder.App.Platform;

namespace UniversalMeetingRecorder.App.UI;

/// <summary>
/// Красная обводка экрана, пока встреча идёт без записи (порт ReminderBorderController): по прозрачному
/// окну на каждый монитор, клики проходят насквозь, поверх всего. Обводка 3 px, радиус 10, красный 85%.
/// Визуальный дубль напоминания, который не зависит ни от уведомлений, ни от «Не беспокоить».
/// </summary>
internal sealed class ReminderBorderController
{
    private readonly List<Window> _windows = new();

    public void Update(bool visible)
    {
        if (visible)
        {
            if (_windows.Count == 0) Show();
            else foreach (var w in _windows) Win32.BringTopmost(w);
        }
        else
        {
            Hide();
        }
    }

    private void Show()
    {
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                Focusable = false,
                IsHitTestVisible = false,
                Title = "Universal Meeting Recorder — встреча без записи",
                Content = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(0.85 * 255), 0xE5, 0x48, 0x4D)),
                    BorderThickness = new Thickness(3),
                    CornerRadius = new CornerRadius(10),
                },
            };
            window.SourceInitialized += (_, _) =>
                Win32.AddExStyle(window, Win32.WsExTransparent | Win32.WsExLayered | Win32.WsExToolWindow | Win32.WsExNoActivate);
            window.Show();
            var b = screen.Bounds;
            Win32.PlaceTopmost(window, b.X, b.Y, b.Width, b.Height);
            // Переезд окна на монитор с другим масштабом вызывает WM_DPICHANGED, и WPF подгоняет размер
            // под «рекомендованный» — ставим точные пиксельные границы ещё раз, когда это уляжется.
            window.Dispatcher.BeginInvoke(() => Win32.PlaceTopmost(window, b.X, b.Y, b.Width, b.Height));
            _windows.Add(window);
        }
    }

    private void Hide()
    {
        foreach (var w in _windows) w.Close();
        _windows.Clear();
    }
}
