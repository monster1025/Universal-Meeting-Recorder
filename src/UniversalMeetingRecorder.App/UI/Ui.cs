using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UniversalMeetingRecorder.App.UI;

/// <summary>
/// Маленькие фабрики элементов: окна собираются кодом, а не XAML — так весь UI одной встречи
/// (баннеры по статусам, секции задач) читается как порт соответствующего SwiftUI-View.
/// </summary>
internal static class Ui
{
    public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static SolidColorBrush Tint(string key, double opacity)
    {
        var c = ((SolidColorBrush)Brush(key)).Color;
        return new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), c.R, c.G, c.B));
    }

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, Brush? color = null, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = color ?? SystemColors.ControlTextBrush,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Caption(string text) => Text(text, 12, color: Brush("Secondary"));

    public static Button Button(string title, Action onClick, bool enabled = true)
    {
        var b = new Button { Content = title, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0), IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Border Badge(string text, string colorKey) => new()
    {
        Style = (Style)Application.Current.Resources["Badge"],
        Background = Tint(colorKey, 0.2),
        Child = Text(text, 11, color: Brush(colorKey), wrap: false),
    };

    public static Border Banner(string colorKey, params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var c in children)
        {
            if (c is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(c);
        }
        return new Border { Style = (Style)Application.Current.Resources["Banner"], Background = Tint(colorKey, 0.08), Child = panel };
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static GroupBox Group(string header, UIElement content) => new()
    {
        Header = Text(header, 14, FontWeights.SemiBold),
        Content = content,
        Padding = new Thickness(8),
        Margin = new Thickness(0, 0, 0, 12),
    };

    public static ProgressBar Spinner() => new() { IsIndeterminate = true, Width = 80, Height = 6, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>«12 авг. 2026 г., 08:14» — дата с коротким временем.</summary>
    public static string DateTimeShort(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy, HH:mm", Ru);

    public static string TimeShort(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", Ru);

    public static string DayMonth(DateTime utc) => utc.ToLocalTime().ToString("d MMM", Ru);

    public static string Timecode(double seconds)
    {
        var total = (int)seconds;
        return $"{total / 60:00}:{total % 60:00}";
    }

    public static void Activate(Window w)
    {
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true; // вытолкнуть поверх, затем вернуть обычный порядок
        w.Topmost = false;
        w.Focus();
    }
}
