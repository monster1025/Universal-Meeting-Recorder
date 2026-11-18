using System.Runtime.InteropServices;
using UniversalMeetingRecorder.Core.Util;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace UniversalMeetingRecorder.App.Recorder;

/// <summary>Пакет звука от WASAPI: interleaved float32 в формате устройства + QPC-время (100 нс).</summary>
internal readonly record struct AudioPacket(float[] Samples, int Frames, int SampleRate, int Channels, long QpcPosition, bool Discontinuity);

/// <summary>
/// Одна дорожка WASAPI в shared mode: loopback устройства вывода (системный звук) или устройство ввода
/// (микрофон). Свой цикл захвата на примитивах NAudio, а не WasapiCapture: нужен QPC-таймстамп
/// каждого пакета (<c>IAudioCaptureClient::GetBuffer</c>) — по нему выравниваются дорожки
/// и заполняются нулями дыры во времени.
///
/// Устройство по умолчанию может смениться посреди записи (подключили гарнитуру) — тогда поток
/// перезапускается на новом устройстве; приведение формата — забота получателя пакетов.
/// </summary>
internal sealed class WasapiTrackCapture : IDisposable, IMMNotificationClient
{
    private const long BufferDuration100Ns = 1_000_000; // 100 мс
    private const int AudclntBufferflagsSilent = 0x2;

    private readonly DataFlow _flow;
    private readonly Role _role;
    private readonly string _name;
    private readonly Action<AudioPacket> _onPacket;
    private readonly MMDeviceEnumerator _enumerator = new();
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _restartRequested;

    public WasapiTrackCapture(DataFlow flow, Role role, string name, Action<AudioPacket> onPacket)
    {
        _flow = flow;
        _role = role;
        _name = name;
        _onPacket = onPacket;
    }

    public bool IsLoopback => _flow == DataFlow.Render;

    /// <summary>Ошибка запуска на первом устройстве (нет устройства, доступ запрещён) — бросается из Start.</summary>
    public void Start()
    {
        _running = true;
        Exception? startError = null;
        using var started = new ManualResetEventSlim();
        _thread = new Thread(() => Loop(started, e => startError = e)) { IsBackground = true, Name = $"WASAPI {_name}", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        started.Wait(TimeSpan.FromSeconds(5));
        if (startError is not null)
        {
            _running = false;
            throw startError;
        }
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            Log.Error($"WASAPI {_name}: подписка на смену устройства не удалась", ex);
        }
    }

    public void Stop(TimeSpan timeout)
    {
        _running = false;
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch
        {
        }
        if (_thread is { } t && !t.Join(timeout)) Log.Error($"WASAPI {_name}: поток захвата не остановился за {timeout.TotalSeconds} с");
        _thread = null;
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(5));
        _enumerator.Dispose();
    }

    private void Loop(ManualResetEventSlim started, Action<Exception> reportStartError)
    {
        var first = true;
        while (_running)
        {
            try
            {
                RunDevice(first ? started : null);
            }
            catch (Exception ex)
            {
                if (first)
                {
                    reportStartError(ex);
                    started.Set();
                    return;
                }
                // AUDCLNT_E_DEVICE_INVALIDATED и т.п.: устройство пропало — пробуем новое по умолчанию.
                Log.Error($"WASAPI {_name}: поток прерван, перезапуск на устройстве по умолчанию", ex);
                Thread.Sleep(300);
            }
            first = false;
            started.Set();
        }
    }

    private void RunDevice(ManualResetEventSlim? started)
    {
        _restartRequested = false;
        using var device = _enumerator.GetDefaultAudioEndpoint(_flow, _role);
        var client = device.AudioClient;
        try
        {
            var format = client.MixFormat;
            var flags = IsLoopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None;
            client.Initialize(AudioClientShareMode.Shared, flags, BufferDuration100Ns, 0, format, Guid.Empty);
            var capture = client.AudioCaptureClient;
            client.Start();
            Log.Info($"WASAPI {_name}: {device.FriendlyName}, {format.SampleRate} Гц × {format.Channels}, {format.Encoding}/{format.BitsPerSample} бит");
            started?.Set();
            var discontinuity = true; // первый пакет после (пере)запуска — всегда разрыв
            while (_running && !_restartRequested)
            {
                Thread.Sleep(10);
                var packet = capture.GetNextPacketSize();
                while (packet > 0 && _running)
                {
                    var ptr = capture.GetBuffer(out var frames, out var bufferFlags, out _, out var qpc);
                    var samples = ToFloat(ptr, frames, format, ((int)bufferFlags & AudclntBufferflagsSilent) != 0);
                    var disc = discontinuity || bufferFlags.HasFlag(AudioClientBufferFlags.DataDiscontinuity);
                    capture.ReleaseBuffer(frames);
                    discontinuity = false;
                    if (frames > 0) _onPacket(new AudioPacket(samples, frames, format.SampleRate, format.Channels, (long)qpc, disc));
                    packet = capture.GetNextPacketSize();
                }
            }
            client.Stop();
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>Буфер устройства → interleaved float32. Shared mode почти всегда отдаёт IEEE float, но не гарантирует.</summary>
    private static float[] ToFloat(IntPtr ptr, int frames, WaveFormat format, bool silent)
    {
        var count = frames * format.Channels;
        var result = new float[count];
        if (silent || ptr == IntPtr.Zero || count == 0) return result;

        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        if (isFloat && format.BitsPerSample == 32)
        {
            Marshal.Copy(ptr, result, 0, count);
            return result;
        }
        var bytes = new byte[count * format.BitsPerSample / 8];
        Marshal.Copy(ptr, bytes, 0, bytes.Length);
        switch (format.BitsPerSample)
        {
            case 16:
                for (var i = 0; i < count; i++) result[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                break;
            case 24:
                for (var i = 0; i < count; i++)
                {
                    var v = bytes[i * 3] | (bytes[i * 3 + 1] << 8) | ((sbyte)bytes[i * 3 + 2] << 16);
                    result[i] = v / 8388608f;
                }
                break;
            case 32:
                for (var i = 0; i < count; i++) result[i] = BitConverter.ToInt32(bytes, i * 4) / 2147483648f;
                break;
        }
        return result;
    }

    // ----- IMMNotificationClient: смена устройства по умолчанию -----

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == _flow && role == _role) _restartRequested = true;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}
