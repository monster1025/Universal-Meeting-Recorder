using UniversalMeetingRecorder.Core.Audio;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.Core.Meetings;

/// <summary>
/// Сведение сырья встречи в m4a, переименование папки в финальное имя и передача в загрузку.
/// Общий путь для только что закончившегося звонка и для прерванной записи (порт
/// <c>RecordingCoordinator.convert</c>). Вынесен в Core, чтобы логику «что сводить, что делать
/// при отсутствии формата, когда переименовывать» можно было проверить без Windows.
///
/// Порядок важен: сначала сведение прямо в текущую (in-progress) папку, удаление сырья, и только
/// последним шагом — переименование. Обратный порядок при смерти процесса посреди микса оставил бы
/// meeting.json со ссылкой на несуществующее имя.
/// </summary>
public sealed class RecordingFinalizer
{
    public const string ErrorNoAudio = "Нет аудиоданных для сведения";
    public const string ErrorNoMicFormat = "Формат микрофонной дорожки неизвестен (запись сделана старой версией приложения) — свести её нельзя";
    public const string ErrorMergeFailed = "Не удалось свести запись — сырые дорожки сохранены в папке встречи";

    private readonly MeetingStore _store;
    private readonly IAudioMixer _mixer;

    public RecordingFinalizer(MeetingStore store, IAudioMixer mixer)
    {
        _store = store;
        _mixer = mixer;
    }

    /// <summary>Возвращает встречу в статусе recorded (готова к загрузке) или null, если свести не удалось.</summary>
    public Meeting? Convert(Guid meetingId)
    {
        var meeting = _store.Find(meetingId);
        if (meeting is null) return null;
        var folder = _store.FolderPath(meeting);
        var sysPath = Path.Combine(folder, RecordingRecovery.RawSysFileName);
        var micPath = Path.Combine(folder, RecordingRecovery.RawMicFileName);
        var audioPath = Path.Combine(folder, meeting.AudioFileName);

        // Время окончания считается по сырью — строго до его удаления.
        var end = meeting.EndDate ?? RecordingRecovery.EstimateEnd(meeting, folder);

        var hasSys = RecordingRecovery.FileSize(sysPath) > RecordingRecovery.MinUsefulBytes;
        var hasMic = RecordingRecovery.FileSize(micPath) > RecordingRecovery.MinUsefulBytes;
        var hasAudio = RecordingRecovery.FileSize(audioPath) > RecordingRecovery.MinUsefulBytes;

        // m4a сам по себе не значит, что сведение завершено: признак незавершённого — сырьё в папке.
        // Частично оставшееся сырьё (падение между двумя удалениями) не должно затирать готовый m4a.
        var rawIsNewer = new[] { sysPath, micPath }.Any(p =>
            RecordingRecovery.FileSize(p) > RecordingRecovery.MinUsefulBytes && File.Exists(audioPath) &&
            File.GetLastWriteTimeUtc(p) > File.GetLastWriteTimeUtc(audioPath));
        var needsMerge = !hasAudio || (hasSys && hasMic) || rawIsNewer;

        if (needsMerge)
        {
            if (!hasSys && !hasMic)
            {
                MarkInterrupted(meetingId, ErrorNoAudio);
                return null;
            }

            double micRate;
            uint micChannels;
            if (meeting.MicSampleRate is { } rate && meeting.MicChannels is { } ch)
            {
                (micRate, micChannels) = (rate, ch);
            }
            else if (!hasMic)
            {
                (micRate, micChannels) = (48000, 1); // дорожки нет — значение не используется
            }
            else
            {
                Log.Info($"RecordingFinalizer: у «{meeting.FolderName}» нет сохранённого формата микрофона — свести нельзя");
                MarkInterrupted(meetingId, ErrorNoMicFormat);
                return null;
            }

            _store.Update(meetingId, m => m.Status = MeetingStatus.Converting);

            var micOffset = meeting.MicStartOffset ?? RecordingRecovery.EstimateMicOffset(meeting, folder, micRate, micChannels);
            var sysTrack = hasSys
                ? new RawTrack(sysPath, (int)(meeting.SysSampleRate ?? RecordingRecovery.DefaultSysSampleRate), (int)(meeting.SysChannels ?? RecordingRecovery.DefaultSysChannels))
                : null;
            var micTrack = hasMic ? new RawTrack(micPath, (int)micRate, (int)micChannels) : null;

            if (!_mixer.MergeTracks(sysTrack, micTrack, micOffset, audioPath, out var mixError))
            {
                Log.Info($"RecordingFinalizer: не удалось свести «{meeting.FolderName}»: {mixError}");
                MarkInterrupted(meetingId, mixError is null ? ErrorMergeFailed : $"{ErrorMergeFailed} ({mixError})");
                return null;
            }
            TryDelete(sysPath);
            TryDelete(micPath);
        }

        var folderName = meeting.FolderName;
        var finalName = FolderNaming.FinalName(meeting.StartDate, end, meeting.AppName);
        if (folderName != finalName)
        {
            var finalFolder = Path.Combine(_store.RootPath, finalName);
            // Коллизия имён — оставляем текущее уникальное имя: терять данные из-за косметики нельзя.
            if (Directory.Exists(finalFolder) || File.Exists(finalFolder))
            {
                Log.Info($"RecordingFinalizer: папка «{finalName}» уже существует, оставляем «{folderName}»");
            }
            else
            {
                try
                {
                    Directory.Move(folder, finalFolder);
                    folderName = finalName;
                }
                catch (Exception ex)
                {
                    Log.Error("RecordingFinalizer: не удалось переименовать папку встречи", ex);
                }
            }
        }

        return _store.Update(meetingId, m =>
        {
            m.FolderName = folderName;
            m.EndDate = end;
            m.Status = MeetingStatus.Recorded;
            m.ErrorMessage = null;
        });
    }

    public void MarkInterrupted(Guid meetingId, string? error) =>
        _store.Update(meetingId, m =>
        {
            m.Status = MeetingStatus.Interrupted;
            m.ErrorMessage = error;
        });

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Error($"RecordingFinalizer: не удалось удалить {path}", ex);
        }
    }
}
