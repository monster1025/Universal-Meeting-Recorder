using UniversalMeetingRecorder.Core.Util;
using NAudio.CoreAudioApi;

namespace UniversalMeetingRecorder.App.Recorder;

/// <summary>
/// Системный mute микрофона (аналог SystemMicMuteController на Mac, где это HAL-mute): глушится
/// конечная точка устройства ввода, поэтому микрофон молчит и для приложения звонка, и для записи
/// (запись дополнительно зануляется в AudioCapture).
///
/// Глушатся устройства по умолчанию для ОБЕИХ ролей — Console и Communications: Teams и Zoom
/// по умолчанию берут «устройство связи», которое может отличаться от «устройства по умолчанию».
/// Если mute конечной точки не применился — fallback на громкость 0 с запоминанием прежней.
/// Watchdog раз в 0.2 с повторно применяет состояние и перепривязывается при смене устройства
/// (старое размьючивается, новое мьютится) — приложения звонка любят сами снимать mute.
/// </summary>
internal sealed class SystemMicMuteController : IDisposable
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    /// <summary>Устройства, которые мы заглушили: id → прежняя громкость (если глушили громкостью).</summary>
    private readonly Dictionary<string, float?> _muted = new();
    private Timer? _watchdog;
    private bool _isMuted;

    public bool IsMuted
    {
        get
        {
            lock (_gate) return _isMuted;
        }
    }

    /// <summary>false — ни mute, ни громкость не применились (оверлей показывает оранжевый).</summary>
    public bool HardwareMuteSupported { get; private set; } = true;

    public event Action<bool>? MutedChanged;

    public void SetMuted(bool muted)
    {
        bool changed;
        lock (_gate)
        {
            changed = _isMuted != muted;
            _isMuted = muted;
            if (muted)
            {
                Enforce();
                _watchdog ??= new Timer(_ => Tick(), null, WatchdogInterval, WatchdogInterval);
            }
            else
            {
                _watchdog?.Dispose();
                _watchdog = null;
                ReleaseAll();
            }
        }
        if (changed) MutedChanged?.Invoke(muted);
    }

    public void Toggle() => SetMuted(!IsMuted);

    private void Tick()
    {
        lock (_gate)
        {
            if (_isMuted) Enforce();
        }
    }

    private List<MMDevice> DefaultCaptureDevices()
    {
        var result = new List<MMDevice>();
        foreach (var role in new[] { Role.Console, Role.Communications })
        {
            try
            {
                var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                if (result.Any(x => x.ID == d.ID)) d.Dispose();
                else result.Add(d);
            }
            catch
            {
                // устройства ввода нет
            }
        }
        return result;
    }

    private void Enforce()
    {
        var devices = DefaultCaptureDevices();
        try
        {
            var current = devices.Select(d => d.ID).ToHashSet();
            // Смена устройства по умолчанию: старое возвращаем как было.
            foreach (var stale in _muted.Keys.Where(id => !current.Contains(id)).ToList()) Release(stale);

            var anyApplied = false;
            foreach (var device in devices)
            {
                anyApplied |= Apply(device);
            }
            HardwareMuteSupported = devices.Count == 0 || anyApplied;
        }
        finally
        {
            foreach (var d in devices) d.Dispose();
        }
    }

    private bool Apply(MMDevice device)
    {
        var volume = device.AudioEndpointVolume;
        try
        {
            if (!volume.Mute) volume.Mute = true;
            if (volume.Mute)
            {
                _muted.TryAdd(device.ID, null);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"MicMute: mute конечной точки «{device.FriendlyName}» не применился", ex);
        }
        try
        {
            if (!_muted.TryGetValue(device.ID, out var saved) || saved is null)
            {
                _muted[device.ID] = volume.MasterVolumeLevelScalar > 0 ? volume.MasterVolumeLevelScalar : 1f;
            }
            if (volume.MasterVolumeLevelScalar > 0) volume.MasterVolumeLevelScalar = 0;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"MicMute: громкость «{device.FriendlyName}» не применилась", ex);
            return false;
        }
    }

    private void ReleaseAll()
    {
        foreach (var id in _muted.Keys.ToList()) Release(id);
        // Страховка: снять mute с текущих устройств по умолчанию, даже если мы их не помним
        // (защитный unmute на старте/конце записи и при выходе).
        foreach (var device in DefaultCaptureDevices())
        {
            try
            {
                if (device.AudioEndpointVolume.Mute) device.AudioEndpointVolume.Mute = false;
            }
            catch
            {
            }
            device.Dispose();
        }
    }

    private void Release(string id)
    {
        var savedVolume = _muted.GetValueOrDefault(id);
        _muted.Remove(id);
        try
        {
            using var device = _enumerator.GetDevice(id);
            var volume = device.AudioEndpointVolume;
            volume.Mute = false;
            if (savedVolume is { } v && volume.MasterVolumeLevelScalar == 0) volume.MasterVolumeLevelScalar = v;
        }
        catch (Exception ex)
        {
            Log.Error($"MicMute: не удалось вернуть устройство {id}", ex);
        }
    }

    public void Dispose()
    {
        SetMuted(false);
        _enumerator.Dispose();
    }
}
