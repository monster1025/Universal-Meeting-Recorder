using UniversalMeetingRecorder.Core.Settings;
using UniversalMeetingRecorder.Core.Transcription;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.Core.Meetings;

/// <summary>
/// State machine загрузки: recorded → uploading → processing → done (или failed). Переживает
/// убийство приложения: ID записи на сервере пишется в meeting.json сразу после ответа upload,
/// ДО поллинга, поэтому ResumeAll() отличает «не загружалось» от «загружено, продолжать поллинг».
/// Сервисов два (Speakr и НейроМенделеев), работает один: новые записи уходят в выбранный в настройках,
/// а уже загруженные встречи дорабатывает тот сервис, чей ID у них записан.
/// </summary>
public sealed class UploadPipeline
{
    /// <summary>Порог «слишком короткой» записи — проверяется только при первой отправке.</summary>
    public static readonly TimeSpan MinUploadDuration = TimeSpan.FromSeconds(120);

    private readonly MeetingStore _store;
    private readonly AppSettings _settings;
    private readonly Func<string, ITranscriptionBackend?> _backends;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (CancellationTokenSource Cts, Guid Token)> _running = new();

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Встреча дошла до done (транскрипт и саммари уже на диске).</summary>
    public event Action<Guid>? MeetingDone;

    public UploadPipeline(MeetingStore store, AppSettings settings, Func<string, ITranscriptionBackend?>? backends = null)
    {
        _store = store;
        _settings = settings;
        _backends = backends ?? TranscriptionBackendFactory.Create(settings, null);
    }

    /// <summary>Подхватывает всё, что зависло в uploading/processing (после перезапуска).</summary>
    public void ResumeAll()
    {
        foreach (var m in _store.Meetings.Where(m => m.Status is MeetingStatus.Uploading or MeetingStatus.Processing))
        {
            Enqueue(m);
        }
    }

    /// <summary>
    /// Запускает пайплайн встречи (не второй раз параллельно). force обходит проверку на короткую
    /// запись — это ручные действия («выгрузить», «транскрибировать заново»).
    /// </summary>
    public void Enqueue(Meeting meeting, bool force = false)
    {
        lock (_gate)
        {
            if (_running.ContainsKey(meeting.Id)) return;
        }
        if (!force && IsTooShortToUpload(meeting))
        {
            _store.Update(meeting.Id, m =>
            {
                m.Status = MeetingStatus.Skipped;
                m.ErrorMessage = null;
            });
            Log.Info($"UploadPipeline: «{meeting.FolderName}» короче {(int)MinUploadDuration.TotalSeconds} с — в Speakr не отправляем");
            return;
        }
        var token = Guid.NewGuid();
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_running.ContainsKey(meeting.Id)) return;
            _running[meeting.Id] = (cts, token);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Run(meeting.Id, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error($"UploadPipeline: непредвиденная ошибка для {meeting.Id}", ex);
            }
            finally
            {
                lock (_gate)
                {
                    // Чистим за собой, только если нас не заменил более новый запуск.
                    if (_running.TryGetValue(meeting.Id, out var current) && current.Token == token) _running.Remove(meeting.Id);
                }
            }
        });
    }

    /// <summary>Короткая — только ни разу не уезжавшая на сервер запись: начатую работу не обрываем.</summary>
    private bool IsTooShortToUpload(Meeting meeting) =>
        _settings.SkipShortRecordingsEnabled && !meeting.HasRemoteRecording &&
        meeting.Duration is { } d && d < MinUploadDuration;

    public bool IsRunning(Guid meetingId)
    {
        lock (_gate) return _running.ContainsKey(meetingId);
    }

    /// <summary>Останавливает пайплайн встречи (перед повтором или удалением).</summary>
    public void Cancel(Guid meetingId)
    {
        lock (_gate)
        {
            if (_running.Remove(meetingId, out var entry)) entry.Cts.Cancel();
        }
    }

    /// <summary>
    /// Транскрибировать заново: сбрасывает remote id, статус, транскрипт/ноуты и грузит файл с нуля.
    /// Используется и как «Повторить» после failed, и как «Выгрузить в Speakr» для skipped.
    /// Старую запись на сервере не удаляет — как и Mac.
    /// </summary>
    public void Retranscribe(Meeting meeting)
    {
        Cancel(meeting.Id);
        var folder = _store.FolderPath(meeting);
        foreach (var name in new[] { meeting.TranscriptFileName, meeting.TranscriptMarkdownFileName, meeting.SummaryFileName })
        {
            if (name is null) continue;
            try
            {
                File.Delete(Path.Combine(folder, name));
            }
            catch
            {
                // нет файла — нечего удалять
            }
        }
        var saved = _store.Update(meeting.Id, m =>
        {
            m.Status = MeetingStatus.Recorded;
            m.SpeakrRecordingID = null;
            m.MendeleevMeetingId = null;
            m.RemoteStatus = null;
            m.TranscriptFileName = null;
            m.TranscriptMarkdownFileName = null;
            m.SummaryFileName = null;
            m.DeletedRemotely = false;
            m.ErrorMessage = null;
        });
        if (saved is not null) Enqueue(saved, force: true);
    }

    /// <summary>Какой сервис ведёт эту встречу: тот, чей ID записан, иначе — выбранный в настройках.</summary>
    private string KindOf(Meeting m) =>
        m.MendeleevMeetingId is not null ? TranscriptionBackends.Mendeleev
        : m.SpeakrRecordingID is not null ? TranscriptionBackends.Speakr
        : _settings.TranscriptionBackend;

    private static string? RemoteIdOf(Meeting m) =>
        m.MendeleevMeetingId ?? m.SpeakrRecordingID?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void SetRemoteId(Meeting m, string kind, string id)
    {
        if (kind == TranscriptionBackends.Mendeleev) m.MendeleevMeetingId = id;
        else m.SpeakrRecordingID = int.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task Run(Guid meetingId, CancellationToken ct)
    {
        var meeting = _store.Find(meetingId);
        if (meeting is null) return;

        var kind = KindOf(meeting);
        var backend = _backends(kind);
        if (backend is null)
        {
            meeting.Status = MeetingStatus.Failed;
            meeting.ErrorMessage = TranscriptionBackendFactory.NotConfiguredMessage(kind);
            _store.Save(meeting);
            return;
        }

        if (RemoteIdOf(meeting) is null)
        {
            meeting.Status = MeetingStatus.Uploading;
            meeting = _store.Save(meeting);
            try
            {
                // Падение здесь до получения id — при следующем старте загрузка повторится
                // (редкий дубль на сервере приемлем для личного инструмента).
                var id = await backend.Upload(_store.AudioPath(meeting), ct).ConfigureAwait(false);
                SetRemoteId(meeting, kind, id);
                meeting.RemoteStatus = "PENDING";
                meeting.Status = MeetingStatus.Processing;
                meeting = _store.Save(meeting);
            }
            catch (Exception ex)
            {
                // Отмена (повтор/удаление) — состояние уже переписано, сообщать не о чем.
                if (ct.IsCancellationRequested) return;
                meeting.Status = MeetingStatus.Failed;
                meeting.ErrorMessage = $"Загрузка не удалась: {ex.Message}";
                _store.Save(meeting);
                return;
            }
        }

        if (RemoteIdOf(meeting) is not { } remoteId) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await backend.GetStatus(remoteId, ct).ConfigureAwait(false);
                // Пока ждали ответ, встречу могли отправить на повтор или удалить.
                if (ct.IsCancellationRequested) return;
                meeting.RemoteStatus = status.Raw;
                meeting = _store.Save(meeting);

                if (status.State == RemoteState.Completed)
                {
                    await Finish(meeting, remoteId, backend, ct).ConfigureAwait(false);
                    return;
                }
                if (status.State == RemoteState.Failed)
                {
                    meeting.Status = MeetingStatus.Failed;
                    meeting.ErrorMessage = status.Error ?? $"{TranscriptionBackends.Display(kind)} сообщил об ошибке обработки";
                    _store.Save(meeting);
                    return;
                }
                // Принято/в очереди/обрабатывается — продолжаем поллинг.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Недоступность сервиса, истёкший вход (повторится с паузой) или сбой на этапе забора результатов —
                // статус не трогаем, пробуем снова.
                Log.Error($"UploadPipeline: ошибка поллинга для {meetingId}", ex);
            }
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task Finish(Meeting meeting, string remoteId, ITranscriptionBackend backend, CancellationToken ct)
    {
        var folder = _store.FolderPath(meeting);
        var result = await backend.GetResult(remoteId, ct).ConfigureAwait(false);

        AtomicFile.WriteAllBytes(Path.Combine(folder, "transcript.json"), result.TranscriptJson);
        meeting.TranscriptFileName = "transcript.json";

        // Markdown — производный файл: неудача рендера не валит обработку (дочинится на старте).
        if (TranscriptMarkdown.Write(result.TranscriptJson, meeting, folder) is { } markdown)
        {
            meeting.TranscriptMarkdownFileName = markdown;
        }

        AtomicFile.WriteAllText(Path.Combine(folder, "notes.md"), result.Notes);
        meeting.SummaryFileName = "notes.md";
        if (result.Title is { Length: > 0 } title) meeting.Title = title;

        if (ct.IsCancellationRequested) return;
        meeting.Status = MeetingStatus.Done;
        _store.Save(meeting);
        MeetingDone?.Invoke(meeting.Id);

        if (!_settings.DeleteAfterUploadEnabled) return;
        await PerformRemoteDelete(meeting.Id, remoteId, backend).ConfigureAwait(false);
    }

    /// <summary>Повторная попытка удалить запись на сервере (кнопка в карточке встречи).</summary>
    public void DeleteRemotely(Meeting meeting)
    {
        if (RemoteIdOf(meeting) is not { } remoteId || meeting.DeletedRemotely) return;
        if (_backends(KindOf(meeting)) is not { } backend) return;
        _ = Task.Run(() => PerformRemoteDelete(meeting.Id, remoteId, backend));
    }

    /// <summary>Результат удаления сохраняется в meeting.json в обоих исходах — иначе провал был бы невидим.</summary>
    private async Task PerformRemoteDelete(Guid meetingId, string remoteId, ITranscriptionBackend backend)
    {
        string? error = null;
        try
        {
            await backend.Delete(remoteId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"UploadPipeline: не удалось удалить запись {remoteId} на {backend.Kind}", ex);
            error = $"Не удалось удалить запись в {TranscriptionBackends.Display(backend.Kind)}: {ex.Message}";
        }
        _store.Update(meetingId, m =>
        {
            if (error is null)
            {
                m.DeletedRemotely = true;
                m.ErrorMessage = null;
            }
            else
            {
                m.ErrorMessage = error;
            }
        });
    }
}
