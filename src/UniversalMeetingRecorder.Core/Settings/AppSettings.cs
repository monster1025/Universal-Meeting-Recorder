using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniversalMeetingRecorder.Core.LLM;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.Core.Settings;

/// <summary>
/// Настройки приложения — единственная изменяемая глобальная конфигурация. Порт AppSettings.swift:
/// те же ключи и дефолты, только вместо UserDefaults — JSON-файл
/// (<c>%APPDATA%\UniversalMeetingRecorder\settings.json</c>; Debug-сборка — <c>UniversalMeetingRecorder.dev</c>,
/// аналог отдельного bundle id), а вместо Keychain — <see cref="ISecretStore"/>.
/// Каждое изменение сразу пишется на диск. Изменения читаются «живыми» (например, excludedApps
/// проверяется на каждом событии), поэтому перезапуск не нужен — кроме корня записей.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    public const string SpeakrSecretService = "com.monster.UniversalMeetingRecorder.Speakr";
    public const string LlmSecretService = "com.monster.UniversalMeetingRecorder.LLM";
    public const string SecretAccount = "apiKey";
    public const string DefaultLlmModel = "Qwen3.6-27B";
    public const int DefaultTaskDigestMinutes = 10 * 60 + 30;

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ISecretStore _secrets;
    private readonly JsonObject _data;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Папка данных в «Документах» — намеренно со старым именем macOS-приложения (ConferenceRecorder):
    /// записи и база задач у Mac- и Windows-версий общие по формату и могут жить в одной синхронизируемой
    /// папке. Переименование здесь разорвало бы эту совместимость.
    /// </summary>
    public const string SharedDataFolderName = "ConferenceRecorder";

    public static string DefaultRecordingsRootPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), SharedDataFolderName, "recordings");

    public static string DefaultTasksDatabasePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), SharedDataFolderName, "tasks.sqlite");

    /// <param name="path">Файл настроек; null — только в памяти (тесты).</param>
    public AppSettings(string? path, ISecretStore secrets)
    {
        _path = path;
        _secrets = secrets;
        _data = Load(path);
        _speakrApiKey = secrets.Get(SpeakrSecretService, SecretAccount) ?? "";
        _llmApiKey = secrets.Get(LlmSecretService, SecretAccount) ?? "";
    }

    private static JsonObject Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            Log.Error($"AppSettings: не удалось прочитать {path}, начинаю с дефолтов", ex);
            return new JsonObject();
        }
    }

    private void Persist()
    {
        if (_path is null) return;
        string text;
        lock (_gate) text = _data.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        try
        {
            AtomicFile.WriteAllText(_path, text);
        }
        catch (Exception ex)
        {
            Log.Error("AppSettings: не удалось сохранить настройки", ex);
        }
    }

    private T Get<T>(string key, T fallback)
    {
        lock (_gate)
        {
            if (!_data.TryGetPropertyValue(key, out var node) || node is null) return fallback;
            try
            {
                return node.Deserialize<T>() ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }

    private void Set<T>(string key, T value, [CallerMemberName] string? property = null)
    {
        lock (_gate)
        {
            var node = JsonSerializer.SerializeToNode(value);
            if (_data.TryGetPropertyValue(key, out var existing) && JsonNode.DeepEquals(existing, node)) return;
            _data[key] = node;
        }
        Persist();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    // ----- Сервис транскрипции -----

    /// <summary>Куда отправлять записи: «speakr» или «mendeleev». Один из двух; у уже загруженных встреч свой сервис.</summary>
    public string TranscriptionBackend
    {
        get => Get("transcriptionBackend", Transcription.TranscriptionBackends.Speakr) is Transcription.TranscriptionBackends.Mendeleev
            ? Transcription.TranscriptionBackends.Mendeleev
            : Transcription.TranscriptionBackends.Speakr;
        set => Set("transcriptionBackend", value);
    }

    /// <summary>Адрес сервера НейроМенделеева. Токен входа хранится в хранилище секретов (см. MendeleevSession).</summary>
    public string MendeleevBaseUrl
    {
        get => Get("mendeleevBaseURL", "");
        set => Set("mendeleevBaseURL", value);
    }

    // ----- Speakr -----

    public bool DeleteAfterUploadEnabled
    {
        get => Get("deleteAfterUploadEnabled", false);
        set => Set("deleteAfterUploadEnabled", value);
    }

    /// <summary>Не отправлять в Speakr записи короче UploadPipeline.MinUploadDuration.</summary>
    public bool SkipShortRecordingsEnabled
    {
        get => Get("skipShortRecordingsEnabled", true);
        set => Set("skipShortRecordingsEnabled", value);
    }

    public string SpeakrBaseUrl
    {
        get => Get("speakrBaseURL", "");
        set => Set("speakrBaseURL", value);
    }

    private string _speakrApiKey;

    /// <summary>API-ключ Speakr — только в хранилище секретов, никогда не в settings.json.</summary>
    public string SpeakrApiKey
    {
        get => _speakrApiKey;
        set
        {
            if (value == _speakrApiKey) return;
            _speakrApiKey = value;
            if (string.IsNullOrEmpty(value)) _secrets.Delete(SpeakrSecretService, SecretAccount);
            else _secrets.Set(SpeakrSecretService, SecretAccount, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakrApiKey)));
        }
    }

    // ----- Запись -----

    /// <summary>Корень записей. MeetingStore читает его один раз на старте — смена действует после перезапуска.</summary>
    public string RecordingsRootPath
    {
        get => Get("recordingsRootPath", DefaultRecordingsRootPath);
        set => Set("recordingsRootPath", value);
    }

    /// <summary>Приложения, чьё использование микрофона не считается звонком (подстрока, без учёта регистра).</summary>
    public List<string> ExcludedApps
    {
        get => Get("excludedApps", new List<string> { "AquaDictate" });
        set => Set("excludedApps", value);
    }

    /// <summary>Оверлей с кнопкой системного mute во время записи. Выключен: меняет системное состояние.</summary>
    public bool SystemMicMuteOverlayEnabled
    {
        get => Get("systemMicMuteOverlayEnabled", false);
        set => Set("systemMicMuteOverlayEnabled", value);
    }

    /// <summary>Позиция оверлея mute: экранные DIP-координаты левого верхнего угла.</summary>
    public ScreenPoint? MicMuteOverlayOrigin
    {
        get => Get<ScreenPoint?>("micMuteOverlayOrigin", null);
        set => Set("micMuteOverlayOrigin", value);
    }

    // ----- Календарь -----

    /// <summary>
    /// Подключать календарь Outlook. Аналог выданного доступа к календарю на Mac: без явного согласия
    /// приложение не трогает Outlook (и не запускает его в фоне).
    /// </summary>
    public bool OutlookCalendarEnabled
    {
        get => Get("outlookCalendarEnabled", false);
        set => Set("outlookCalendarEnabled", value);
    }

    public bool MeetingReminderEnabled
    {
        get => Get("meetingReminderEnabled", false);
        set => Set("meetingReminderEnabled", value);
    }

    /// <summary>Красная обводка экрана — визуальный дубль напоминания, включена по умолчанию.</summary>
    public bool ReminderBorderEnabled
    {
        get => Get("reminderBorderEnabled", true);
        set => Set("reminderBorderEnabled", value);
    }

    /// <summary>Календари, за которыми следим. Пустой список намеренно значит «все».</summary>
    public List<string> ReminderCalendarIds
    {
        get => Get("reminderCalendarIDs", new List<string>());
        set => Set("reminderCalendarIDs", value);
    }

    // ----- Задачи / LLM -----

    public bool ExtractTasksEnabled
    {
        get => Get("extractTasksEnabled", false);
        set => Set("extractTasksEnabled", value);
    }

    public string LlmBaseUrl
    {
        get => Get("llmBaseURL", "");
        set => Set("llmBaseURL", value);
    }

    public string LlmModel
    {
        get => Get("llmModel", DefaultLlmModel);
        set => Set("llmModel", value);
    }

    private string _llmApiKey;

    public string LlmApiKey
    {
        get => _llmApiKey;
        set
        {
            if (value == _llmApiKey) return;
            _llmApiKey = value;
            if (string.IsNullOrEmpty(value)) _secrets.Delete(LlmSecretService, SecretAccount);
            else _secrets.Set(LlmSecretService, SecretAccount, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LlmApiKey)));
        }
    }

    /// <summary>Как пользователя зовут на встречах — уходит в промпт.</summary>
    public List<string> MySpeakerNames
    {
        get => Get("mySpeakerNames", new List<string>());
        set => Set("mySpeakerNames", value);
    }

    public bool TaskDigestEnabled
    {
        get => Get("taskDigestEnabled", true);
        set => Set("taskDigestEnabled", value);
    }

    /// <summary>Время сводки — минуты от полуночи (630 = 10:30).</summary>
    public int TaskDigestMinutes
    {
        get => Get("taskDigestMinutes", DefaultTaskDigestMinutes);
        set => Set("taskDigestMinutes", value);
    }

    public string? TaskDigestLastSentDay
    {
        get => Get<string?>("taskDigestLastSentDay", null);
        set => Set("taskDigestLastSentDay", value);
    }

    /// <summary>Фильтр окна задач: all / personal / work.</summary>
    public string TasksScopeFilter
    {
        get => Get("tasksScopeFilter", "all");
        set => Set("tasksScopeFilter", value);
    }

    // ----- Виджет задач (Windows-аналог WidgetKit-виджета) -----

    public bool TaskWidgetEnabled
    {
        get => Get("taskWidgetEnabled", false);
        set => Set("taskWidgetEnabled", value);
    }

    /// <summary>all / personal / work — как интент виджета на Mac.</summary>
    public string TaskWidgetFilter
    {
        get => Get("taskWidgetFilter", "all");
        set => Set("taskWidgetFilter", value);
    }

    /// <summary>Сколько задач показывать (1–10, по умолчанию 5).</summary>
    public int TaskWidgetLimit
    {
        get => Math.Clamp(Get("taskWidgetLimit", 5), 1, 10);
        set => Set("taskWidgetLimit", Math.Clamp(value, 1, 10));
    }

    public ScreenPoint? TaskWidgetOrigin
    {
        get => Get<ScreenPoint?>("taskWidgetOrigin", null);
        set => Set("taskWidgetOrigin", value);
    }

    /// <summary>Клиент LLM по текущим настройкам; null, если чего-то не хватает.</summary>
    public OpenAiCompatibleClient? MakeLlmClient() => OpenAiCompatibleClient.Create(LlmBaseUrl, LlmApiKey, LlmModel);
}

public sealed record ScreenPoint(double X, double Y);
