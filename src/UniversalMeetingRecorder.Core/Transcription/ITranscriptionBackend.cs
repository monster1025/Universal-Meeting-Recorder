namespace UniversalMeetingRecorder.Core.Transcription;

/// <summary>Сервисы, куда можно отправлять записи. Выбирается один — настройкой «Сервис транскрипции».</summary>
public static class TranscriptionBackends
{
    public const string Speakr = "speakr";
    public const string Mendeleev = "mendeleev";

    public static string Display(string kind) => kind == Mendeleev ? "НейроМенделеев" : "Speakr";
}

public enum RemoteState
{
    /// <summary>Принято или обрабатывается — продолжаем опрос.</summary>
    Pending,
    Completed,
    Failed,
}

/// <param name="Raw">Статус сервера как есть — показывается в карточке встречи.</param>
public sealed record RemoteStatus(RemoteState State, string Raw, string? Error);

/// <param name="TranscriptJson">
/// Транскрипт в формате Speakr (<c>{"segments":[{speaker,sentence,start_time,end_time}]}</c>): именно так он лежит
/// в transcript.json у всех встреч, и его читают macOS-версия и извлечение задач — независимо от сервиса.
/// </param>
/// <param name="Notes">Митинг-ноуты (summary у Speakr, протокол у НейроМенделеева) — идёт в notes.md.</param>
public sealed record RemoteResult(byte[] TranscriptJson, string Notes, string? Title);

/// <summary>Исключение, после которого повтор запроса бессмысленно без действий пользователя (нет входа, неверный ключ).</summary>
public sealed class RemoteAuthException : Exception
{
    public RemoteAuthException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// То, что нужно UploadPipeline от сервиса транскрипции: загрузить → опрашивать → забрать результат → (удалить).
/// ID записи у сервисов разные по типу (число у Speakr, UUID у НейроМенделеева) — для пайплайна это строка.
/// </summary>
public interface ITranscriptionBackend
{
    string Kind { get; }
    Task<string> Upload(string filePath, CancellationToken ct);
    Task<RemoteStatus> GetStatus(string remoteId, CancellationToken ct);
    Task<RemoteResult> GetResult(string remoteId, CancellationToken ct);
    /// <summary>Удаляет запись на сервере и убеждается, что её там больше нет.</summary>
    Task Delete(string remoteId, CancellationToken ct);
}
