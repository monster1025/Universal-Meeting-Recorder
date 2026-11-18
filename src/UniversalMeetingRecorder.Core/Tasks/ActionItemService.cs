using UniversalMeetingRecorder.Core.LLM;
using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Settings;
using UniversalMeetingRecorder.Core.Speakr;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.Core.Tasks;

/// <summary>
/// Связывает экстрактор с приложением: запускает извлечение после обработки встречи, пишет
/// результат в meeting.json, применяет решение пользователя. Ошибки — в extractionError встречи.
/// </summary>
public sealed class ActionItemService
{
    private readonly MeetingStore _store;
    private readonly TaskStore _taskStore;
    private readonly AppSettings _settings;
    private readonly Func<ILlmProvider?> _llmFactory;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _running = new();

    /// <summary>Есть непустые предложения — окно разбора ставит встречу в очередь.</summary>
    public event Action<Guid>? SuggestionsReady;

    public Func<ILlmProvider, ActionItemExtractor> ExtractorFactory { get; init; } = llm => new ActionItemExtractor(llm);

    public ActionItemService(MeetingStore store, TaskStore taskStore, AppSettings settings, Func<ILlmProvider?>? llmFactory = null)
    {
        _store = store;
        _taskStore = taskStore;
        _settings = settings;
        _llmFactory = llmFactory ?? (() => settings.MakeLlmClient());
    }

    public bool IsRunning(Guid meetingId)
    {
        lock (_gate) return _running.Contains(meetingId);
    }

    /// <summary>Автозапуск после done. Молчит, если фича выключена.</summary>
    public void ExtractIfEnabled(Guid meetingId)
    {
        if (_settings.ExtractTasksEnabled) Extract(meetingId);
    }

    /// <summary>На старте: процесс мог умереть посреди запроса, тогда в meeting.json остался pending.</summary>
    public void ResumePending()
    {
        if (!_settings.ExtractTasksEnabled) return;
        foreach (var m in _store.Meetings.Where(m => m.ExtractionState == ExtractionState.Pending)) Extract(m.Id);
    }

    /// <summary>Запуск (в том числе ручной). Не стартует второй раз параллельно для той же встречи.</summary>
    public Task Extract(Guid meetingId)
    {
        lock (_gate)
        {
            if (_running.Contains(meetingId)) return Task.CompletedTask;
        }
        var meeting = _store.Find(meetingId);
        if (meeting is null) return Task.CompletedTask;

        // Конфигурацию проверяем до pending: иначе «не настроено» зависало бы вечным ожиданием.
        var client = _llmFactory();
        if (client is null)
        {
            Fail(meetingId, "LLM не настроена: заполните адрес, модель и ключ во вкладке «Задачи» настроек");
            return Task.CompletedTask;
        }
        var names = _settings.MySpeakerNames;
        if (names.Count == 0)
        {
            Fail(meetingId, "Укажите, как вас зовут на встречах, во вкладке «Задачи» настроек");
            return Task.CompletedTask;
        }
        List<SpeakrTranscriptSegment> segments;
        try
        {
            var path = _store.TranscriptPath(meeting) ?? throw new FileNotFoundException();
            segments = SpeakrTranscriptResponse.Parse(File.ReadAllBytes(path)).Segments;
        }
        catch
        {
            Fail(meetingId, "Нет транскрипта встречи");
            return Task.CompletedTask;
        }
        string? notes = null;
        try
        {
            if (_store.NotesPath(meeting) is { } np && File.Exists(np)) notes = File.ReadAllText(np);
        }
        catch
        {
            // ноуты — только подсказка
        }

        lock (_gate)
        {
            if (!_running.Add(meetingId)) return Task.CompletedTask;
        }
        meeting.ExtractionState = ExtractionState.Pending;
        meeting.ExtractionError = null;
        meeting = _store.Save(meeting);

        // Уже созданные из этой встречи задачи и отклонённые предложения повторно не предлагаем.
        var exclude = _taskStore.TitlesLinkedToMeeting(meetingId).Concat(meeting.DismissedSuggestions ?? new List<string>()).ToList();
        var snapshot = meeting;

        return Task.Run(async () =>
        {
            try
            {
                var suggestions = await ExtractorFactory(client)
                    .Extract(snapshot, segments, notes, names, exclude)
                    .ConfigureAwait(false);
                // Встречу перечитываем: пока ждали LLM, она могла измениться.
                _store.Update(meetingId, m =>
                {
                    m.SuggestedTasks = suggestions;
                    m.ExtractionState = ExtractionState.Done;
                    m.ExtractionError = null;
                    m.ActionItemsReviewed = suggestions.Count == 0;
                });
                if (suggestions.Count > 0) SuggestionsReady?.Invoke(meetingId);
            }
            catch (Exception ex)
            {
                Log.Error($"ActionItemService: извлечение для {meetingId}", ex);
                Fail(meetingId, ex.Message);
            }
            finally
            {
                lock (_gate) _running.Remove(meetingId);
            }
        });
    }

    /// <summary>
    /// Создаёт выбранные задачи, остальные отклоняет. Оригинальные заголовки всех предложений
    /// запоминаются (нормализованными) — повторный прогон их не вернёт. false — база отказала.
    /// </summary>
    public bool Resolve(Guid meetingId, IReadOnlyList<TaskDraft> accepted)
    {
        var meeting = _store.Find(meetingId);
        if (meeting is null) return false;
        if (!_taskStore.AddTasks(accepted, meeting)) return false;
        _store.Update(meetingId, m =>
        {
            var titles = (m.SuggestedTasks ?? new List<SuggestedTask>()).Select(t => ActionItemExtractor.Normalize(t.Title));
            m.DismissedSuggestions = (m.DismissedSuggestions ?? new List<string>()).Concat(titles).Distinct().ToList();
            m.SuggestedTasks = new List<SuggestedTask>();
            m.ActionItemsReviewed = true;
        });
        return true;
    }

    public void DismissAll(Guid meetingId) => Resolve(meetingId, Array.Empty<TaskDraft>());

    public void MarkReviewed(Guid meetingId)
    {
        var meeting = _store.Find(meetingId);
        if (meeting is null || meeting.ActionItemsReviewed == true) return;
        _store.Update(meetingId, m => m.ActionItemsReviewed = true);
    }

    /// <summary>Сколько непросмотренных предложений по всем встречам — для пункта меню трея.</summary>
    public static int UnreviewedCount(IEnumerable<Meeting> meetings) =>
        meetings.Where(m => m.ActionItemsReviewed != true).Sum(m => m.SuggestedTasks?.Count ?? 0);

    private void Fail(Guid meetingId, string message) =>
        _store.Update(meetingId, m =>
        {
            m.ExtractionState = ExtractionState.Failed;
            m.ExtractionError = message;
        });
}
