using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UniversalMeetingRecorder.App.Platform;
using UniversalMeetingRecorder.App.Recorder;
using UniversalMeetingRecorder.Core.Settings;

namespace UniversalMeetingRecorder.App.UI;

/// <summary>
/// Плавающая кнопка системного mute на время записи (порт MicMuteOverlay): капсула 72×22,
/// зелёная — микрофон включён, красная — заглушён, оранжевая с треугольником — mute не применился.
/// Клик переключает, перетаскивание двигает (тап — если сдвиг меньше 3 px), позиция запоминается.
/// Окно не активируется (WS_EX_NOACTIVATE) — фокус остаётся в приложении звонка.
/// При скрытии оверлея mute принудительно снимается.
/// </summary>
internal sealed class MicMuteOverlay
{
    private readonly AppSettings _settings;
    private readonly RecordingCoordinator _coordinator;
    private readonly SystemMicMuteController _mute;
    private Window? _window;
    private Border? _capsule;
    private TextBlock? _label;
    private Polygon? _warning;

    public MicMuteOverlay(AppSettings settings, RecordingCoordinator coordinator, SystemMicMuteController mute)
    {
        _settings = settings;
        _coordinator = coordinator;
        _mute = mute;
    }

    public void Start()
    {
        _coordinator.PropertyChanged += OnChanged;
        _settings.PropertyChanged += OnChanged;
        _mute.MutedChanged += _ => Application.Current.Dispatcher.BeginInvoke(Refresh);
        Sync();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Application.Current.Dispatcher.BeginInvoke(Sync);

    private void Sync()
    {
        var shouldShow = _coordinator.IsRecording && _settings.SystemMicMuteOverlayEnabled;
        if (shouldShow && _window is null) Show();
        else if (!shouldShow && _window is not null) Hide();
    }

    private void Show()
    {
        _label = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        var icon = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        _warning = new Polygon
        {
            Points = new PointCollection { new(5, 0), new(10, 9), new(0, 9) },
            Fill = Brushes.Orange,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -4, 0),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { icon, _label } };
        _capsule = new Border { Width = 72, Height = 22, CornerRadius = new CornerRadius(11), Child = row, Cursor = Cursors.Hand };
        var root = new Grid { Margin = new Thickness(6), Children = { _capsule, _warning } };
        root.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.35 };

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = root,
            Title = "Mute",
        };
        window.SourceInitialized += (_, _) => Win32.AddExStyle(window, Win32.WsExToolWindow | Win32.WsExNoActivate);
        AttachDrag(window, root);

        if (_settings.MicMuteOverlayOrigin is { } p && OnScreen(p.X, p.Y))
        {
            window.Left = p.X;
            window.Top = p.Y;
        }
        else
        {
            // По умолчанию — сверху по центру рабочей области основного экрана, на 40 пт ниже края.
            var area = SystemParameters.WorkArea;
            window.Left = area.Left + (area.Width - 84) / 2;
            window.Top = area.Top + 40;
        }
        _window = window;
        Refresh();
        window.Show();
    }

    private static bool OnScreen(double x, double y) =>
        x >= SystemParameters.VirtualScreenLeft && y >= SystemParameters.VirtualScreenTop &&
        x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 20 &&
        y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 10;

    private void AttachDrag(Window window, FrameworkElement root)
    {
        Point? downScreen = null;
        Point downOrigin = default;
        var moved = false;
        root.MouseLeftButtonDown += (_, e) =>
        {
            downScreen = window.PointToScreen(e.GetPosition(window));
            downOrigin = new Point(window.Left, window.Top);
            moved = false;
            root.CaptureMouse();
        };
        root.MouseMove += (_, e) =>
        {
            if (downScreen is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var now = window.PointToScreen(e.GetPosition(window));
            var source = PresentationSource.FromVisual(window);
            var scale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            var dx = (now.X - start.X) * scale;
            var dy = (now.Y - start.Y) * scale;
            if (!moved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;
            moved = true;
            window.Left = downOrigin.X + dx;
            window.Top = downOrigin.Y + dy;
        };
        root.MouseLeftButtonUp += (_, _) =>
        {
            root.ReleaseMouseCapture();
            if (downScreen is null) return;
            downScreen = null;
            if (moved) _settings.MicMuteOverlayOrigin = new ScreenPoint(window.Left, window.Top);
            else _mute.Toggle();
        };
    }

    private void Refresh()
    {
        if (_capsule is null || _label is null || _warning is null) return;
        var muted = _mute.IsMuted;
        var unsupported = muted && !_mute.HardwareMuteSupported;
        _label.Text = muted ? "Unmute" : "Mute";
        _capsule.Background = new SolidColorBrush(unsupported
            ? Color.FromRgb(0xF0, 0x8C, 0x00)
            : muted ? Color.FromRgb(0xE5, 0x48, 0x4D) : Color.FromRgb(0x2F, 0x9E, 0x44));
        _warning.Visibility = unsupported ? Visibility.Visible : Visibility.Collapsed;
        _capsule.ToolTip = muted ? "Микрофон заглушён в системе — нажмите, чтобы включить" : "Заглушить микрофон в системе";
    }

    private void Hide()
    {
        _mute.SetMuted(false);
        _window?.Close();
        _window = null;
        _capsule = null;
        _label = null;
        _warning = null;
    }
}
