using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.Core.Meetings;

/// <summary>Перемещение папки в Корзину — платформенная операция (на Windows — Recycle Bin).</summary>
public interface IRecycleBin
{
    void MoveToRecycleBin(string directoryPath);
}

/// <summary>
/// Список встреч. Источник истины — файловая система: meeting.json в каждой подпапке корня.
/// Без базы и без кэша сложнее одного Refresh(). Потокобезопасен: сохранение идёт из фоновых
/// задач (загрузка, сведение), чтение — из UI. Отдаёт копии, чтобы никто не мутировал общее.
/// </summary>
public sealed class MeetingStore
{
    private readonly object _gate = new();
    private List<Meeting> _meetings = new();
    private Guid? _pendingSelection;

    public string RootPath { get; }

    /// <summary>Список изменился (вызывается на произвольном потоке).</summary>
    public event Action? Changed;

    /// <summary>Встречу нужно выделить в окне «Встречи» (переход из задачи).</summary>
    public event Action<Guid>? SelectionRequested;

    public MeetingStore(string rootPath)
    {
        RootPath = rootPath;
        try
        {
            Directory.CreateDirectory(rootPath);
        }
        catch (Exception ex)
        {
            Log.Error($"MeetingStore: не удалось создать корень записей {rootPath}", ex);
        }
    }

    public IReadOnlyList<Meeting> Meetings
    {
        get
        {
            lock (_gate) return _meetings.Select(m => m.Clone()).ToList();
        }
    }

    public Meeting? Find(Guid id)
    {
        lock (_gate) return _meetings.FirstOrDefault(m => m.Id == id)?.Clone();
    }

    public Guid? TakePendingSelection()
    {
        lock (_gate)
        {
            var value = _pendingSelection;
            _pendingSelection = null;
            return value;
        }
    }

    public void RequestSelection(Guid id)
    {
        lock (_gate) _pendingSelection = id;
        SelectionRequested?.Invoke(id);
    }

    public void Refresh()
    {
        var loaded = new List<Meeting>();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(RootPath))
            {
                var metaPath = Path.Combine(folder, RecordingRecovery.MetaFileName);
                if (!File.Exists(metaPath)) continue;
                try
                {
                    var meeting = MeetingJson.Deserialize(File.ReadAllText(metaPath));
                    // Имя папки — с диска, а не из JSON: переименование папки и запись meeting.json —
                    // две операции, смерть процесса между ними оставила бы в JSON несуществующий путь.
                    meeting.FolderName = Path.GetFileName(folder);
                    loaded.Add(meeting);
                }
                catch (Exception ex)
                {
                    Log.Error($"MeetingStore: пропускаю {metaPath}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"MeetingStore: не удалось прочитать {RootPath}", ex);
        }
        lock (_gate) _meetings = loaded.OrderByDescending(m => m.StartDate).ToList();
        Changed?.Invoke();
    }

    public string FolderPath(Meeting m) => Path.Combine(RootPath, m.FolderName);
    public string AudioPath(Meeting m) => Path.Combine(FolderPath(m), m.AudioFileName);
    public string? TranscriptPath(Meeting m) => m.TranscriptFileName is { } n ? Path.Combine(FolderPath(m), n) : null;
    public string? TranscriptMarkdownPath(Meeting m) => m.TranscriptMarkdownFileName is { } n ? Path.Combine(FolderPath(m), n) : null;
    public string? NotesPath(Meeting m) => m.SummaryFileName is { } n ? Path.Combine(FolderPath(m), n) : null;

    /// <summary>
    /// Дописывает transcript.md встречам, у которых его нет. Вызывается один раз на старте после Refresh().
    /// </summary>
    public void BackfillTranscriptMarkdown()
    {
        var written = 0;
        foreach (var meeting in Meetings)
        {
            if (TranscriptPath(meeting) is not { } transcriptPath || !File.Exists(transcriptPath)) continue;
            var folder = FolderPath(meeting);
            if (meeting.TranscriptMarkdownFileName is not null && File.Exists(Path.Combine(folder, TranscriptMarkdown.FileName))) continue;
            byte[] data;
            try
            {
                data = File.ReadAllBytes(transcriptPath);
            }
            catch
            {
                continue;
            }
            if (TranscriptMarkdown.Write(data, meeting, folder) is not { } name) continue;
            meeting.TranscriptMarkdownFileName = name;
            Save(meeting);
            written++;
        }
        if (written > 0) Log.Info($"MeetingStore: собрано transcript.md для {written} встреч(и)");
    }

    /// <summary>Папка встречи уезжает в Корзину (запись может быть единственной копией разговора).</summary>
    public bool Delete(Meeting meeting, IRecycleBin recycleBin)
    {
        var folder = FolderPath(meeting);
        if (Directory.Exists(folder))
        {
            try
            {
                recycleBin.MoveToRecycleBin(folder);
            }
            catch (Exception ex)
            {
                Log.Error($"MeetingStore: не удалось удалить папку встречи {folder}", ex);
                return false;
            }
        }
        lock (_gate) _meetings.RemoveAll(m => m.Id == meeting.Id);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Атомарная запись meeting.json + обновление в памяти. Возвращает сохранённую копию.</summary>
    public Meeting Save(Meeting meeting)
    {
        var updated = meeting.Clone();
        updated.LastUpdated = DateTime.UtcNow;
        var folder = FolderPath(updated);
        try
        {
            AtomicFile.WriteAllText(Path.Combine(folder, RecordingRecovery.MetaFileName), MeetingJson.Serialize(updated));
        }
        catch (Exception ex)
        {
            Log.Error("MeetingStore: не удалось сохранить meeting.json", ex);
            return meeting;
        }
        lock (_gate)
        {
            var index = _meetings.FindIndex(m => m.Id == updated.Id);
            if (index >= 0)
            {
                _meetings[index] = updated;
            }
            else
            {
                _meetings.Add(updated);
                _meetings = _meetings.OrderByDescending(m => m.StartDate).ToList();
            }
        }
        Changed?.Invoke();
        return updated.Clone();
    }

    /// <summary>Читает встречу, применяет изменение и сохраняет — если она ещё существует.</summary>
    public Meeting? Update(Guid id, Action<Meeting> change)
    {
        var meeting = Find(id);
        if (meeting is null) return null;
        change(meeting);
        return Save(meeting);
    }
}
