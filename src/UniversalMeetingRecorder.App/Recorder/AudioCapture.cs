using UniversalMeetingRecorder.Core.Audio;
using UniversalMeetingRecorder.Core.Util;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace UniversalMeetingRecorder.App.Recorder;

/// <summary>
/// Захват одной встречи: системный звук (loopback) + микрофон в два сырых файла interleaved float32
/// (sys.f32le / mic.f32le), как на Mac. Аналог SCAudioCapture.
///
/// КРИТИЧНО (аналог бага с ускорением ×2 на Mac): WASAPI loopback НЕ отдаёт пакетов, пока система
/// ничего не играет. Без мер против этого системная дорожка оказалась бы короче реального времени,
/// и выравнивание с микрофоном поехало бы на длину всех пауз. Две меры:
///  1) на всё время записи на устройство вывода играет тишина (keep-alive render-поток) — тогда
///     аудиодвижок крутится и loopback отдаёт нулевые пакеты непрерывно;
///  2) страховка: разрыв между QPC-временем пакета и ожидаемым концом записанного заполняется нулями
///     (покрывает и смену устройства посреди звонка).
/// Проверять изменения здесь — записью с длинными паузами и сверкой длительности файла с реальным временем.
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    /// <summary>Разрыв меньше этого — джиттер таймстампов, а не дыра; не заполняем.</summary>
    private const double GapThresholdSeconds = 0.1;
    /// <summary>Защита от мусорных таймстампов: дыру больше часа не заполняем.</summary>
    private const double MaxGapSeconds = 3600;

    private readonly object _gate = new();
    private readonly Track _sys;
    private readonly Track _mic;
    private readonly SilenceDetector _silence = new();
    private WasapiTrackCapture? _sysCapture;
    private WasapiTrackCapture? _micCapture;
    private WasapiOut? _keepAlive;
    private bool _offsetReported;
    private volatile bool _muted;

    /// <summary>Формат микрофона из первого буфера — сразу в meeting.json.</summary>
    public event Action<int, int>? MicFormatResolved;
    /// <summary>Формат системной дорожки из первого буфера (только Windows: на Mac он фиксированный).</summary>
    public event Action<int, int>? SysFormatResolved;
    /// <summary>Насколько микрофон начат позже системной дорожки, секунды.</summary>
    public event Action<double>? MicOffsetResolved;
    /// <summary>Микрофон отдаёт цифровую тишину ≥ 5 с (или перестал).</summary>
    public event Action<bool>? MicSilenceChanged;

    public AudioCapture(string sysPath, string micPath)
    {
        _sys = new Track(sysPath, maxChannels: 2);
        _mic = new Track(micPath, maxChannels: 2);
    }

    public bool IsMuted
    {
        get => _muted;
        set => _muted = value;
    }

    public (int Rate, int Channels)? MicFormat => _mic.Format;
    public (int Rate, int Channels)? SysFormat => _sys.Format;

    public double? MicStartOffset
    {
        get
        {
            lock (_gate)
            {
                return _sys.FirstQpc is { } s && _mic.FirstQpc is { } m ? (m - s) / 1e7 : null;
            }
        }
    }

    /// <summary>
    /// Запуск. Ошибка обоих потоков — исключение (запись не идёт). Ошибка одного — запись идёт
    /// с одной дорожкой (как на Mac: нет микрофона — сводится одна системная).
    /// </summary>
    public void Start()
    {
        StartKeepAlive();
        Exception? sysError = null, micError = null;
        try
        {
            _sysCapture = new WasapiTrackCapture(DataFlow.Render, Role.Multimedia, "system", p => OnPacket(_sys, p, isMic: false));
            _sysCapture.Start();
        }
        catch (Exception ex)
        {
            sysError = ex;
            _sysCapture = null;
            Log.Error("AudioCapture: системный звук недоступен", ex);
        }
        try
        {
            _micCapture = new WasapiTrackCapture(DataFlow.Capture, Role.Console, "mic", p => OnPacket(_mic, p, isMic: true));
            _micCapture.Start();
        }
        catch (Exception ex)
        {
            micError = ex;
            _micCapture = null;
            Log.Error("AudioCapture: микрофон недоступен", ex);
        }
        if (sysError is not null && micError is not null)
        {
            StopKeepAlive();
            throw new InvalidOperationException($"нет ни системного звука, ни микрофона ({micError.Message})", micError);
        }
    }

    /// <summary>Тишина на устройство вывода, чтобы loopback не замолкал в паузах (см. комментарий класса).</summary>
    private void StartKeepAlive()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _keepAlive = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
            var silence = new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(device.AudioClient.MixFormat.SampleRate, 2));
            _keepAlive.Init(silence);
            _keepAlive.Play();
        }
        catch (Exception ex)
        {
            // Не фатально: дыры закроет заполнение по QPC.
            Log.Error("AudioCapture: keep-alive поток тишины не запустился", ex);
            _keepAlive?.Dispose();
            _keepAlive = null;
        }
    }

    private void StopKeepAlive()
    {
        try
        {
            _keepAlive?.Stop();
            _keepAlive?.Dispose();
        }
        catch
        {
        }
        _keepAlive = null;
    }

    private void OnPacket(Track track, AudioPacket packet, bool isMic)
    {
        float[]? silenceInput = null;
        bool? silenceChange = null;
        bool firstFormat;
        double? offset = null;
        lock (_gate)
        {
            if (track.Closed) return;
            firstFormat = track.Format is null;
            var samples = track.Adapt(packet);
            var frames = samples.Length / track.Format!.Value.Channels;

            if (track.FirstQpc is null)
            {
                track.FirstQpc = packet.QpcPosition;
            }
            else
            {
                // Дыра во времени (тишина loopback без keep-alive, смена устройства) → нули.
                var expected = track.FirstQpc.Value + (long)(track.FramesWritten * 1e7 / track.Format.Value.Rate);
                var gapSeconds = (packet.QpcPosition - expected) / 1e7;
                if (gapSeconds > GapThresholdSeconds && gapSeconds < MaxGapSeconds)
                {
                    track.WriteSilence((long)Math.Round(gapSeconds * track.Format.Value.Rate));
                }
            }

            if (isMic)
            {
                silenceInput = samples;
                if (_muted) samples = new float[samples.Length]; // нули той же длины: файл линеен во времени
            }
            track.Write(samples, frames);

            if (!_offsetReported && _sys.FirstQpc is { } s && _mic.FirstQpc is { } m)
            {
                _offsetReported = true;
                offset = (m - s) / 1e7;
            }
        }

        if (firstFormat)
        {
            var f = track.Format!.Value;
            if (isMic) MicFormatResolved?.Invoke(f.Rate, f.Channels);
            else SysFormatResolved?.Invoke(f.Rate, f.Channels);
        }
        if (offset is { } o) MicOffsetResolved?.Invoke(o);
        if (silenceInput is not null)
        {
            // По сырому сигналу устройства, до зануления mute.
            silenceChange = _silence.Feed(silenceInput, track.Format!.Value.Channels, track.Format.Value.Rate, _muted);
            if (silenceChange is { } silent) MicSilenceChanged?.Invoke(silent);
        }
    }

    public void Stop()
    {
        var timeout = TimeSpan.FromSeconds(5);
        var stops = new[] { _sysCapture, _micCapture }
            .Where(c => c is not null)
            .Select(c => Task.Run(() => c!.Stop(timeout)))
            .ToArray();
        Task.WaitAll(stops, timeout);
        lock (_gate)
        {
            _sys.Close();
            _mic.Close();
        }
        StopKeepAlive();
    }

    public void Dispose()
    {
        Stop();
        _sysCapture?.Dispose();
        _micCapture?.Dispose();
    }

    /// <summary>Сырой файл одной дорожки. Формат фиксируется первым пакетом.</summary>
    private sealed class Track
    {
        private readonly string _path;
        private readonly int _maxChannels;
        private FileStream? _stream;
        private FormatAdapter? _adapter;
        private (int Rate, int Channels) _adapterInput;

        public (int Rate, int Channels)? Format { get; private set; }
        public long? FirstQpc { get; set; }
        public long FramesWritten { get; private set; }
        public bool Closed { get; private set; }

        public Track(string path, int maxChannels)
        {
            _path = path;
            _maxChannels = maxChannels;
        }

        public float[] Adapt(AudioPacket p)
        {
            if (Format is null)
            {
                Format = (p.SampleRate, Math.Min(p.Channels, _maxChannels));
            }
            var input = (p.SampleRate, p.Channels);
            if (_adapter is null || _adapterInput != input)
            {
                _adapter = new FormatAdapter(p.SampleRate, p.Channels, Format.Value.Rate, Format.Value.Channels);
                _adapterInput = input;
            }
            return _adapter.IsIdentity ? p.Samples : _adapter.Convert(p.Samples, p.Frames);
        }

        public void Write(float[] samples, int frames)
        {
            if (Closed) return;
            _stream ??= new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            var bytes = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            _stream.Write(bytes);
            FramesWritten += frames;
        }

        public void WriteSilence(long frames)
        {
            if (Format is null || frames <= 0) return;
            var ch = Format.Value.Channels;
            const int chunkFrames = 48000;
            var chunk = new float[chunkFrames * ch];
            while (frames > 0)
            {
                var n = (int)Math.Min(frames, chunkFrames);
                if (n == chunkFrames) Write(chunk, n);
                else Write(new float[n * ch], n);
                frames -= n;
            }
        }

        public void Close()
        {
            Closed = true;
            try
            {
                _stream?.Flush();
                _stream?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error($"AudioCapture: закрытие {_path}", ex);
            }
            _stream = null;
        }
    }
}
