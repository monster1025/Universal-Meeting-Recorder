namespace UniversalMeetingRecorder.Core.Audio;

/// <summary>Платформонезависимая арифметика сведения и детектора тишины (порт из AudioPipeline/SCAudioCapture).</summary>
public static class MixMath
{
    public const int OutputSampleRate = 48000;
    public const int OutputChannels = 2;
    public const int AacBitrate = 192_000;

    /// <summary>Смещение больше этого — заведомо мусор (сломанные таймстампы), выравнивание не делаем.</summary>
    public const double MaxTrackOffsetSeconds = 30;

    /// <summary>Пик |x| ≤ 1e-4 (≈ −80 dBFS) — буфер «тихий».</summary>
    public const float SilencePeakThreshold = 1e-4f;
    /// <summary>Сколько секунд подряд тишины считается отказом микрофона.</summary>
    public const double SilenceWarningSeconds = 5.0;

    /// <summary>
    /// Сдвиг микрофонной дорожки в кадрах выходной частоты. Положительный — микрофон начат позже
    /// системной дорожки, и его нужно задержать; отрицательный — задержать системную.
    /// Ноль вместо реального смещения — не косметика: звук из динамиков попадает в обе дорожки,
    /// и сдвиг микрофона в прошлое слышен как эхо.
    /// </summary>
    public static long OffsetFrames(double offsetSeconds)
    {
        if (!double.IsFinite(offsetSeconds) || Math.Abs(offsetSeconds) > MaxTrackOffsetSeconds) return 0;
        return (long)Math.Round(offsetSeconds * OutputSampleRate);
    }

    public static float Clip(float x) => x > 1f ? 1f : x < -1f ? -1f : x;

    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        return peak;
    }
}

/// <summary>
/// Детектор «микрофон отдаёт цифровую тишину» (наблюдалось с Bluetooth-гарнитурами). Считается по
/// сырому сигналу устройства. Сообщает только изменения состояния.
/// </summary>
public sealed class SilenceDetector
{
    private double _silentSeconds;
    private bool _silent;

    /// <summary>Возвращает новое состояние, если оно изменилось, иначе null.</summary>
    public bool? Feed(ReadOnlySpan<float> interleaved, int channels, double sampleRate, bool muted)
    {
        if (muted)
        {
            _silentSeconds = 0;
            return Set(false);
        }
        if (MixMath.Peak(interleaved) <= MixMath.SilencePeakThreshold)
        {
            _silentSeconds += interleaved.Length / (double)Math.Max(1, channels) / sampleRate;
            return _silentSeconds >= MixMath.SilenceWarningSeconds ? Set(true) : null;
        }
        _silentSeconds = 0;
        return Set(false);
    }

    private bool? Set(bool value)
    {
        if (_silent == value) return null;
        _silent = value;
        return value;
    }
}

/// <summary>Сведение двух сырых дорожек в m4a — реализуется платформой (на Windows — NAudio + Media Foundation).</summary>
public interface IAudioMixer
{
    /// <summary>
    /// Читает raw interleaved float32 файлы (любой может отсутствовать), ресемплирует в 48 кГц/стерео,
    /// выравнивает по смещению, суммирует с клипом ±1 и кодирует AAC 192 кбит/с. false — не получилось.
    /// </summary>
    bool MergeTracks(RawTrack? sys, RawTrack? mic, double micOffsetSeconds, string outputPath, out string? error);
}

public sealed record RawTrack(string Path, int SampleRate, int Channels);
