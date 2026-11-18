namespace UniversalMeetingRecorder.Core.Meetings;

/// <summary>Статусы встречи — строки в meeting.json совпадают с macOS-версией.</summary>
public enum MeetingStatus
{
    /// <summary>Запись идёт: meeting.json уже на диске, сырьё пишется, m4a ещё нет.</summary>
    Recording,
    /// <summary>Сырьё сводится в m4a. После падения процесса трактуется как прерванное.</summary>
    Converting,
    /// <summary>Сырьё есть, сведения не случилось. Сводится при следующем запуске.</summary>
    Interrupted,
    Recorded,
    /// <summary>Сведено, но намеренно не выгружено в Speakr (короче порога).</summary>
    Skipped,
    Uploading,
    Processing,
    Done,
    Failed,
}

public enum ExtractionState
{
    Pending,
    Done,
    Failed,
}

/// <summary>Кандидат в задачи от LLM. Живёт в meeting.json, в базу задач попадает только подтверждённое.</summary>
public sealed class SuggestedTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    /// <summary>Дословная цитата из транскрипта — основание для задачи.</summary>
    public string Quote { get; set; } = "";
    /// <summary>Начало реплики с цитатой, секунды (по локальному совпадению, не от модели).</summary>
    public double? Timestamp { get; set; }
    /// <summary>Срок, названный в разговоре (только при найденной цитате про срок).</summary>
    public DateTime? DueDate { get; set; }
    /// <summary>Цитата найдена в транскрипте; иначе задача предлагается неотмеченной.</summary>
    public bool Confirmed { get; set; }
    public double? Confidence { get; set; }

    public SuggestedTask Clone() => (SuggestedTask)MemberwiseClone();
}

/// <summary>
/// Схема meeting.json — источник истины для одной встречи. Создаётся в момент СТАРТА записи
/// (а не по завершении): только так идущая запись видна в списке и переживает падение процесса.
/// Набор полей и их имена совпадают с <c>Meeting.swift</c>; Windows добавляет два своих
/// опциональных поля (sysSampleRate/sysChannels), которые Mac молча игнорирует.
/// </summary>
public sealed class Meeting
{
    public const string DefaultAudioFileName = "recording.m4a";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string AppName { get; set; } = "";
    /// <summary>Заголовок от Speakr. null — показываем AppName.</summary>
    public string? Title { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string FolderName { get; set; } = "";
    public string AudioFileName { get; set; } = DefaultAudioFileName;
    public MeetingStatus Status { get; set; } = MeetingStatus.Recorded;
    public string? RemoteStatus { get; set; }
    public int? SpeakrRecordingID { get; set; }
    public string? TranscriptFileName { get; set; }
    public string? TranscriptMarkdownFileName { get; set; }
    public string? SummaryFileName { get; set; }
    public bool DeletedRemotely { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Формат микрофонной дорожки — известен только из первого буфера. Пишется на диск сразу:
    /// без него сырьё нельзя свести после падения (rate × channels по размеру файла неоднозначно).
    /// </summary>
    public double? MicSampleRate { get; set; }
    public uint? MicChannels { get; set; }
    /// <summary>Насколько микрофон начат позже системной дорожки, секунды (может быть отрицательным).</summary>
    public double? MicStartOffset { get; set; }

    /// <summary>
    /// Только Windows: формат системной (loopback) дорожки. На Mac она всегда 48 кГц/стерео,
    /// на Windows пишется в mix-формате устройства вывода (бывает 44.1 кГц). null — 48000/2.
    /// </summary>
    public double? SysSampleRate { get; set; }
    public uint? SysChannels { get; set; }

    public ExtractionState? ExtractionState { get; set; }
    public string? ExtractionError { get; set; }
    public List<SuggestedTask>? SuggestedTasks { get; set; }
    public bool? ActionItemsReviewed { get; set; }
    /// <summary>Нормализованные заголовки уже разобранных предложений.</summary>
    public List<string>? DismissedSuggestions { get; set; }

    /// <summary>Единственное место, решающее, что показывать как имя встречи.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? AppName : Title!;

    /// <summary>Дошла ли запись до сведённого m4a.</summary>
    public bool HasEncodedAudio => Status is not (MeetingStatus.Recording or MeetingStatus.Converting or MeetingStatus.Interrupted);

    public TimeSpan? Duration => EndDate is { } end ? end - StartDate : null;

    public Meeting Clone()
    {
        var copy = (Meeting)MemberwiseClone();
        copy.SuggestedTasks = SuggestedTasks?.Select(t => t.Clone()).ToList();
        copy.DismissedSuggestions = DismissedSuggestions?.ToList();
        return copy;
    }
}
