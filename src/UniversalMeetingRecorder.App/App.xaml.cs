using System.Windows;
using UniversalMeetingRecorder.App.Platform;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.App;

/// <summary>
/// Приложение живёт в трее (окон при старте нет, ShutdownMode=OnExplicitShutdown). Один экземпляр:
/// второй запуск тихо выходит до создания каких-либо сервисов — иначе два детектора начали бы
/// писать одну и ту же встречу.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstance;
    private static bool _ownsMutex;
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\" + AppPaths.AppId + ".SingleInstance", out var isFirst);
        _ownsMutex = isFirst;
        if (!isFirst)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
        Log.Configure(AppPaths.LogDirectory);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Необработанное исключение UI", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Необработанное исключение", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Необработанное исключение задачи", args.Exception);
            args.SetObserved();
        };

        _services = new AppServices(Dispatcher);
        _services.Start();
    }

    /// <summary>Выход из Windows (логофф/выключение) — то же, что «Выход» из меню.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _services?.Shutdown();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Shutdown();
        _services?.Dispose();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        base.OnExit(e);
    }
}
