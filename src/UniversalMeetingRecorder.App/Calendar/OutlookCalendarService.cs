using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using UniversalMeetingRecorder.Core.Calendar;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.App.Calendar;

/// <summary>
/// Календарь из классического Outlook через COM (аналог EventKit на Mac). Позднее связывание
/// (<c>dynamic</c>) — без interop-сборок и без привязки к версии Office. Все COM-вызовы — на одном
/// выделенном STA-потоке.
///
/// Ограничения (см. README): «новый Outlook» (olk.exe) COM не поддерживает; чтение Body может вызвать
/// окно Object Model Guard, если антивирус в системе не считается «актуальным».
/// В отличие от EventKit, кэша нет — каждый запрос свежий, аналог reset() на EKEventStoreChanged не нужен.
/// </summary>
internal sealed class OutlookCalendarService : IDisposable
{
    private const int OlFolderCalendar = 9;
    private const int OlAppointmentItem = 1;
    private const int OlResponseDeclined = 4;
    private const int OlMeetingCanceled = 5;
    private const int OlMeetingReceivedAndCanceled = 7;
    private static readonly TimeSpan FolderCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private dynamic? _app;
    private List<(CalendarFeed Feed, dynamic Folder)>? _folders;
    private DateTime _foldersLoadedAt;

    /// <summary>Человекочитаемое состояние подключения для настроек.</summary>
    public string Status { get; private set; } = "Outlook ещё не опрашивался";
    public bool IsConnected { get; private set; }
    public IReadOnlyList<CalendarFeed> Feeds { get; private set; } = Array.Empty<CalendarFeed>();

    public event Action? Changed;

    public OutlookCalendarService()
    {
        _thread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error("Outlook: ошибка на COM-потоке", ex);
                }
            }
        }) { IsBackground = true, Name = "Outlook COM" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private Task<T> Run<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>Перечитать список календарей (кнопка в настройках, открытие вкладки).</summary>
    public Task ReloadFeeds() => Run(() =>
    {
        _folders = null;
        LoadFolders();
        return true;
    });

    /// <summary>Встречи, идущие сейчас. Пустой calendarIds — все календари; выбор есть, но ничего не резолвится — пусто.</summary>
    public Task<List<OngoingMeeting>> OngoingMeetings(IReadOnlyCollection<string> calendarIds, DateTime nowUtc) => Run(() =>
    {
        var folders = LoadFolders();
        if (folders is null) return new List<OngoingMeeting>();
        var selected = calendarIds.Count == 0 ? folders : folders.Where(f => calendarIds.Contains(f.Feed.Id)).ToList();
        var events = new List<CalendarEventData>();
        foreach (var (feed, folder) in selected)
        {
            try
            {
                events.AddRange(EventsAround(folder, feed.Id, nowUtc));
            }
            catch (Exception ex)
            {
                Log.Error($"Outlook: чтение календаря «{feed.Title}»", ex);
                _folders = null; // папка могла пропасть — перечитаем в следующий раз
            }
        }
        return OngoingMeetingFilter.Filter(events, nowUtc);
    });

    private List<(CalendarFeed Feed, dynamic Folder)>? LoadFolders()
    {
        if (_folders is not null && DateTime.UtcNow - _foldersLoadedAt < FolderCacheLifetime) return _folders;
        var app = Connect();
        if (app is null) return null;
        var result = new List<(CalendarFeed, dynamic)>();
        try
        {
            dynamic session = app.Session;
            dynamic stores = session.Stores;
            int count = stores.Count;
            for (var i = 1; i <= count; i++)
            {
                dynamic store = stores[i];
                string storeName = SafeString(() => (string)store.DisplayName) ?? "Outlook";
                string storeId = SafeString(() => (string)store.StoreID) ?? storeName;
                dynamic? root = null;
                try
                {
                    root = store.GetDefaultFolder(OlFolderCalendar);
                }
                catch
                {
                    // у хранилища нет календаря (архив, общие папки)
                }
                if (root is null) continue;
                Collect(root, storeId, storeName, result);
            }
            Status = $"Outlook подключён: календарей — {result.Count}";
            IsConnected = true;
        }
        catch (Exception ex)
        {
            Log.Error("Outlook: перечисление календарей", ex);
            Status = $"Ошибка Outlook: {ex.Message}";
            IsConnected = false;
            _app = null;
        }
        _folders = result;
        _foldersLoadedAt = DateTime.UtcNow;
        Feeds = result.Select(r => r.Item1).OrderBy(f => f.SourceTitle).ThenBy(f => f.Title).ToList();
        Changed?.Invoke();
        return result;
    }

    private static void Collect(dynamic folder, string storeId, string storeName, List<(CalendarFeed, dynamic)> into)
    {
        try
        {
            if ((int)folder.DefaultItemType != OlAppointmentItem) return;
            string entryId = folder.EntryID;
            string name = folder.Name;
            into.Add((new CalendarFeed($"{storeId}|{entryId}", name, storeName), folder));
            dynamic children = folder.Folders;
            int n = children.Count;
            for (var i = 1; i <= n; i++) Collect(children[i], storeId, storeName, into);
        }
        catch (Exception ex)
        {
            Log.Error("Outlook: обход папки календаря", ex);
        }
    }

    /// <summary>
    /// События, пересекающие «сейчас ± 1 минута». Restrict с IncludeRecurrences требует Sort("[Start]")
    /// до него; даты в фильтре — в формате локали пользователя (так их понимает Outlook). Точная
    /// проверка «идёт прямо сейчас» — в OngoingMeetingFilter, а не фильтром Outlook.
    /// </summary>
    private static IEnumerable<CalendarEventData> EventsAround(dynamic folder, string feedId, DateTime nowUtc)
    {
        var nowLocal = nowUtc.ToLocalTime();
        var from = nowLocal.AddMinutes(-1).ToString("g", CultureInfo.CurrentCulture);
        var to = nowLocal.AddMinutes(1).ToString("g", CultureInfo.CurrentCulture);
        dynamic items = folder.Items;
        items.IncludeRecurrences = true;
        items.Sort("[Start]");
        dynamic restricted = items.Restrict($"[Start] <= '{to}' AND [End] >= '{from}'");
        var result = new List<CalendarEventData>();
        var guard = 0;
        foreach (dynamic item in restricted)
        {
            // Повторяющиеся без конца с IncludeRecurrences перечисляются бесконечно — ограничиваем.
            if (++guard > 200) break;
            try
            {
                DateTime start = item.Start;
                DateTime end = item.End;
                if (start > nowLocal.AddMinutes(1)) break; // отсортировано по началу
                string key = SafeString(() => (string)item.GlobalAppointmentID) is { Length: > 0 } gid ? gid : (string)item.EntryID;
                int meetingStatus = item.MeetingStatus;
                int response = item.ResponseStatus;
                result.Add(new CalendarEventData(
                    Key: $"{feedId}|{key}",
                    Title: SafeString(() => (string)item.Subject) ?? "",
                    StartUtc: DateTime.SpecifyKind(start, DateTimeKind.Local).ToUniversalTime(),
                    EndUtc: DateTime.SpecifyKind(end, DateTimeKind.Local).ToUniversalTime(),
                    IsAllDay: (bool)item.AllDayEvent,
                    IsCancelled: meetingStatus is OlMeetingCanceled or OlMeetingReceivedAndCanceled,
                    IsDeclined: response == OlResponseDeclined,
                    Url: null,
                    Location: SafeString(() => (string)item.Location),
                    Notes: SafeString(() => (string)item.Body)));
            }
            finally
            {
                Marshal.FinalReleaseComObject(item);
            }
        }
        return result;
    }

    private dynamic? Connect()
    {
        if (_app is not null)
        {
            try
            {
                _ = _app.Version; // жив ли
                return _app;
            }
            catch
            {
                _app = null;
            }
        }
        var type = Type.GetTypeFromProgID("Outlook.Application");
        if (type is null)
        {
            SetDisconnected("Классический Outlook не установлен (новый Outlook не поддерживает подключение)");
            return null;
        }
        try
        {
            _app = GetActiveObject("Outlook.Application") ?? Activator.CreateInstance(type);
            return _app;
        }
        catch (Exception ex)
        {
            SetDisconnected($"Не удалось подключиться к Outlook: {ex.Message}");
            return null;
        }
    }

    private void SetDisconnected(string status)
    {
        var changed = Status != status || IsConnected;
        Status = status;
        IsConnected = false;
        Feeds = Array.Empty<CalendarFeed>();
        if (changed) Changed?.Invoke();
    }

    private static string? SafeString(Func<string?> get)
    {
        try
        {
            return get();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);

    /// <summary>Marshal.GetActiveObject в .NET 5+ нет — подключение к уже запущенному Outlook вручную.</summary>
    private static object? GetActiveObject(string progId)
    {
        if (CLSIDFromProgID(progId, out var clsid) != 0) return null;
        return GetActiveObject(ref clsid, IntPtr.Zero, out var obj) == 0 ? obj : null;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
    }
}
