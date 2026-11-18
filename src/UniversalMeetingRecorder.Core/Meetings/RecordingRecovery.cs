namespace UniversalMeetingRecorder.Core.Meetings;

/// <summary>
/// Вспомогательное для записей, не доживших до сведения (падение, taskkill, таймаут на выходе).
/// Чистые функции над файловой системой — про UI, Speakr и хранилище ничего не знают.
/// Формат микрофона никогда не подбирается: только сохранённый в meeting.json.
/// </summary>
public static class RecordingRecovery
{
    public const string RawSysFileName = "sys.f32le";
    public const string RawMicFileName = "mic.f32le";
    public const string MetaFileName = "meeting.json";

    /// <summary>Формат системной дорожки по умолчанию (как всегда на Mac): 48 кГц, стерео.</summary>
    public const double DefaultSysSampleRate = 48000;
    public const uint DefaultSysChannels = 2;

    /// <summary>Порог «в файле что-то есть».</summary>
    public const long MinUsefulBytes = 100;

    public static long FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static double SysBytesPerSecond(Meeting m) =>
        (m.SysSampleRate ?? DefaultSysSampleRate) * (m.SysChannels ?? DefaultSysChannels) * 4.0;

    /// <summary>
    /// Время окончания прерванной записи: по длительности системной дорожки, иначе по mtime
    /// m4a/микрофона, иначе — время старта. Вызывать до удаления сырых дорожек.
    /// </summary>
    public static DateTime EstimateEnd(Meeting meeting, string folder)
    {
        var start = meeting.StartDate;
        var sysBytes = FileSize(Path.Combine(folder, RawSysFileName));
        if (sysBytes > MinUsefulBytes)
        {
            return start.AddSeconds(sysBytes / SysBytesPerSecond(meeting));
        }
        foreach (var name in new[] { Meeting.DefaultAudioFileName, RawMicFileName })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            var modified = File.GetLastWriteTimeUtc(path);
            if (modified > start) return modified;
        }
        return start;
    }

    /// <summary>
    /// Оценка смещения микрофона для записей без сохранённого micStartOffset: обе дорожки перестают
    /// писаться одновременно, поэтому разница длительностей — задержка старта микрофона.
    /// </summary>
    public static double EstimateMicOffset(Meeting meeting, string folder, double micSampleRate, uint micChannels)
    {
        var sysBytes = FileSize(Path.Combine(folder, RawSysFileName));
        var micBytes = FileSize(Path.Combine(folder, RawMicFileName));
        if (sysBytes <= MinUsefulBytes || micBytes <= MinUsefulBytes || micSampleRate <= 0 || micChannels == 0) return 0;
        var micBytesPerSecond = micSampleRate * micChannels * 4.0;
        return sysBytes / SysBytesPerSecond(meeting) - micBytes / micBytesPerSecond;
    }
}
