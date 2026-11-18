using System.ComponentModel;
using System.Windows.Threading;
using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Settings;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.App.Recorder;

/// <summary>
/// Склеивает детектор звонка, захват и сведение, владеет жизненным циклом папки встречи (порт
/// RecordingCoordinator.swift). Всё состояние меняется на UI-потоке (аналог @MainActor): события
/// детектора и захвата приходят с фоновых потоков и перекидываются через Dispatcher.
///
/// meeting.json пишется в момент старта записи. Ручную запись событие «микрофон освобождён»
/// не останавливает — иначе конец постороннего звонка обрывал бы запись, которую попросили вести.
/// Автостарт при уже идущей записи игнорируется — вторая запись поверх первой потеряла бы её захват.
/// </summary>
internal sealed class RecordingCoordinator : INotifyPropertyChanged
{
    /// <summary>Имя встречи, записанной вручную вне звонка.</summary>
    public const string ManualAppName = "Ручная запись";

    private readonly Dispatcher _ui;
    private readonly MeetingStore _store;
    private readonly AppSettings _settings;
    private readonly UploadPipeline _upload;
    private readonly SystemMicMuteController _mute;
    private readonly RecordingFinalizer _finalizer;
    private readonly MicUsageWatcher _watcher;
    private readonly object _conversionsGate = new();
    private readonly Dictionary<Guid, Task> _conversions = new();

    private AudioCapture? _capture;
    private Guid? _activeMeetingId;
    private bool _isManualRecording;
    private bool _micInputSilent;
    private bool _isRecording;
    private string? _currentAppName;
    private bool _micSilenceWarning;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RecordingCoordinator(Dispatcher ui, MeetingStore store, AppSettings settings, UploadPipeline upload, SystemMicMuteController mute)
    {
        _ui = ui;
        _store = store;
        _settings = settings;
        _upload = upload;
        _mute = mute;
        _finalizer = new RecordingFinalizer(store, new AudioMixer());
        _watcher = new MicUsageWatcher(() => _settings.ExcludedApps);
        _watcher.ConferenceStarted += name => _ui.BeginInvoke(() => HandleStart(name));
        _watcher.ConferenceEnded += () => _ui.BeginInvoke(HandleEnd);
        // Состояние mute — в активный захват в реальном времени (оверлей переключают посреди звонка).
        _mute.MutedChanged += muted => _ui.BeginInvoke(() =>
        {
            if (_capture is not null) _capture.IsMuted = muted;
            UpdateMicSilenceWarning();
        });
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set => SetField(ref _isRecording, value, nameof(IsRecording));
    }

    public string? CurrentAppName
    {
        get => _currentAppName;
        private set => SetField(ref _currentAppName, value, nameof(CurrentAppName));
    }

    /// <summary>
    /// Микрофон отдаёт цифровую тишину, и это не наш mute — голос в запись не попадает.
    /// Иконка в трее краснеет, пока состояние держится.
    /// </summary>
    public bool MicSilenceWarning
    {
        get => _micSilenceWarning;
        private set => SetField(ref _micSilenceWarning, value, nameof(MicSilenceWarning));
    }

    public void Start() => _watcher.Start();

    public void Stop() => _watcher.Stop();

    public void StartManually()
    {
        if (IsRecording) return;
        BeginRecording(_watcher.ActiveAppName ?? ManualAppName, manual: true);
    }

    /// <summary>Ручная остановка — останавливает и автоматическую запись тоже.</summary>
    public void StopManually()
    {
        if (IsRecording) FinishRecording();
    }

    /// <summary>Для выхода из приложения: останавливает запись и возвращает задачу её сведения.</summary>
    public Task? StopActiveRecordingIfNeeded()
    {
        if (!IsRecording) return null;
        var id = FinishRecording();
        if (id is null) return null;
        lock (_conversionsGate) return _conversions.GetValueOrDefault(id.Value);
    }

    /// <summary>
    /// Подхватывает записи, не дожившие до сведения. Вызывать один раз на старте СТРОГО до Start():
    /// активных записей в этот момент быть не может, всё в recording/converting — прервано.
    /// Сводит последовательно (бережём память и диск).
    /// </summary>
    public void RecoverUnfinished()
    {
        foreach (var m in _store.Meetings.Where(m => m.Status is MeetingStatus.Recording or MeetingStatus.Converting))
        {
            _store.Update(m.Id, x => x.Status = MeetingStatus.Interrupted);
        }
        var pending = _store.Meetings.Where(m => m.Status == MeetingStatus.Interrupted).Select(m => m.Id).ToList();
        if (pending.Count == 0) return;
        Log.Info($"RecordingCoordinator: незавершённых записей к сведению — {pending.Count}");
        _ = Task.Run(() =>
        {
            foreach (var id in pending) ConvertAndEnqueue(id);
        });
    }

    private void HandleStart(string appName)
    {
        if (IsRecording)
        {
            Log.Info($"RecordingCoordinator: «{appName}» открыл микрофон во время идущей записи — продолжаем текущую");
            return;
        }
        BeginRecording(appName, manual: false);
    }

    private void HandleEnd()
    {
        if (_isManualRecording)
        {
            Log.Info("RecordingCoordinator: микрофон освобождён, но запись начата вручную — продолжаем");
            return;
        }
        if (IsRecording) FinishRecording();
    }

    private void BeginRecording(string appName, bool manual)
    {
        // Новый звонок не должен молча начаться замьюченным.
        _mute.SetMuted(false);
        var start = DateTime.UtcNow;
        IsRecording = true;
        _isManualRecording = manual;
        CurrentAppName = appName;
        _micInputSilent = false;
        UpdateMicSilenceWarning();

        // Секунды в имени: две записи могут начаться в одну минуту. Существующую папку не переиспользуем.
        var name = FolderNaming.InProgressName(start, appName);
        if (Directory.Exists(Path.Combine(_store.RootPath, name))) name += "-" + Guid.NewGuid().ToString("D").ToUpperInvariant()[..8];
        var folder = Path.Combine(_store.RootPath, name);
        Directory.CreateDirectory(folder);

        var meeting = _store.Save(new Meeting
        {
            AppName = appName,
            StartDate = start,
            FolderName = name,
            Status = MeetingStatus.Recording,
        });
        var meetingId = meeting.Id;
        _activeMeetingId = meetingId;

        var capture = new AudioCapture(Path.Combine(folder, RecordingRecovery.RawSysFileName), Path.Combine(folder, RecordingRecovery.RawMicFileName));
        _capture = capture;
        capture.MicFormatResolved += (rate, ch) => _ui.BeginInvoke(() => StoreFormat(meetingId, mic: true, rate, ch));
        capture.SysFormatResolved += (rate, ch) => _ui.BeginInvoke(() => StoreFormat(meetingId, mic: false, rate, ch));
        capture.MicOffsetResolved += offset => _ui.BeginInvoke(() => StoreOffset(meetingId, offset));
        capture.MicSilenceChanged += silent => _ui.BeginInvoke(() => SetMicInputSilent(silent));

        _ = Task.Run(() =>
        {
            try
            {
                capture.Start();
            }
            catch (Exception ex)
            {
                Log.Error("RecordingCoordinator: захват звука недоступен", ex);
                _ui.BeginInvoke(() => HandleCaptureFailure(meetingId, ex.Message));
            }
        });
    }

    /// <summary>Захват не запустился — снимаем «идёт запись», иначе трей показывал бы несуществующую запись.</summary>
    private void HandleCaptureFailure(Guid meetingId, string message)
    {
        if (_activeMeetingId != meetingId) return;
        _capture?.Stop();
        _capture = null;
        ResetState();
        _finalizer.MarkInterrupted(meetingId, $"Не удалось начать захват звука: {message}");
    }

    private void StoreFormat(Guid meetingId, bool mic, int rate, int channels)
    {
        if (_activeMeetingId != meetingId) return;
        _store.Update(meetingId, m =>
        {
            if (mic)
            {
                m.MicSampleRate = rate;
                m.MicChannels = (uint)channels;
            }
            else
            {
                m.SysSampleRate = rate;
                m.SysChannels = (uint)channels;
            }
        });
    }

    private void StoreOffset(Guid meetingId, double offset)
    {
        if (_activeMeetingId != meetingId) return;
        _store.Update(meetingId, m => m.MicStartOffset = offset);
    }

    /// <summary>После остановки записи в полёте могут прийти ещё буферы — вне записи игнорируем.</summary>
    private void SetMicInputSilent(bool silent)
    {
        if (!IsRecording) return;
        _micInputSilent = silent;
        UpdateMicSilenceWarning();
    }

    /// <summary>Красная иконка — только когда молчание не объясняется нажатым mute.</summary>
    private void UpdateMicSilenceWarning() => MicSilenceWarning = IsRecording && _micInputSilent && !_mute.IsMuted;

    private Guid? FinishRecording()
    {
        var capture = _capture;
        var meetingId = _activeMeetingId;
        var meeting = meetingId is { } id ? _store.Find(id) : null;
        if (capture is null || meeting is null)
        {
            ResetState();
            return null;
        }
        capture.Stop();
        // mute не должен незаметно перейти на следующую встречу.
        _mute.SetMuted(false);
        var micFormat = capture.MicFormat;
        var sysFormat = capture.SysFormat;
        var offset = capture.MicStartOffset;
        _capture = null;
        ResetState();

        var saved = _store.Update(meeting.Id, m =>
        {
            m.EndDate = DateTime.UtcNow;
            if (micFormat is { } mf)
            {
                m.MicSampleRate = mf.Rate;
                m.MicChannels = (uint)mf.Channels;
            }
            if (sysFormat is { } sf)
            {
                m.SysSampleRate = sf.Rate;
                m.SysChannels = (uint)sf.Channels;
            }
            m.MicStartOffset = offset ?? m.MicStartOffset;
        });
        if (saved is null) return null;
        var task = Task.Run(() => ConvertAndEnqueue(saved.Id));
        lock (_conversionsGate) _conversions[saved.Id] = task;
        return saved.Id;
    }

    private void ConvertAndEnqueue(Guid meetingId)
    {
        try
        {
            if (_finalizer.Convert(meetingId) is { } recorded) _upload.Enqueue(recorded);
        }
        catch (Exception ex)
        {
            Log.Error($"RecordingCoordinator: сведение {meetingId}", ex);
            _finalizer.MarkInterrupted(meetingId, RecordingFinalizer.ErrorMergeFailed);
        }
        finally
        {
            lock (_conversionsGate) _conversions.Remove(meetingId);
        }
    }

    private void ResetState()
    {
        IsRecording = false;
        _isManualRecording = false;
        CurrentAppName = null;
        _activeMeetingId = null;
        _micInputSilent = false;
        UpdateMicSilenceWarning();
    }

    private void SetField<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
