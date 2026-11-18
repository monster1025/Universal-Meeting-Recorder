using UniversalMeetingRecorder.Core.Audio;
using UniversalMeetingRecorder.Core.Util;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace UniversalMeetingRecorder.App.Recorder;

/// <summary>
/// Сведение двух сырых дорожек в recording.m4a (аналог AudioPipeline): ресемпл в 48 кГц/стерео,
/// выравнивание по смещению (позже начатая дорожка дополняется тишиной в начале), сумма с клипом ±1,
/// AAC 192 кбит/с через Media Foundation. Потоково, без загрузки дорожек в память целиком.
/// В отличие от Mac, ffmpeg не нужен и здесь: кодер — системный (нет в Windows «N» без Media Feature Pack).
/// </summary>
internal sealed class AudioMixer : IAudioMixer
{
    public bool MergeTracks(RawTrack? sys, RawTrack? mic, double micOffsetSeconds, string outputPath, out string? error)
    {
        error = null;
        var streams = new List<Stream>();
        var tmp = Path.Combine(Path.GetDirectoryName(outputPath)!, "recording.tmp.m4a");
        try
        {
            var offsetFrames = MixMath.OffsetFrames(micOffsetSeconds);
            var inputs = new List<ISampleProvider>();
            if (sys is not null) inputs.Add(Open(sys, streams, offsetFrames < 0 ? -offsetFrames : 0));
            if (mic is not null) inputs.Add(Open(mic, streams, offsetFrames > 0 ? offsetFrames : 0));
            if (inputs.Count == 0)
            {
                error = "нет дорожек";
                return false;
            }
            ISampleProvider mixed = inputs.Count == 1 ? inputs[0] : new MixingSampleProvider(inputs) { ReadFully = false };
            var clipped = new ClipSampleProvider(mixed);

            MediaFoundationApi.Startup();
            if (File.Exists(tmp)) File.Delete(tmp);
            // AAC-кодер Media Foundation принимает 16-битный PCM; float на входе работает не везде.
            MediaFoundationEncoder.EncodeToAac(new SampleToWaveProvider16(clipped), tmp, MixMath.AacBitrate);
            foreach (var s in streams) s.Dispose();
            streams.Clear();
            File.Move(tmp, outputPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("AudioMixer: сведение не удалось", ex);
            error = ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0xC00D36D5) or unchecked((int)0x80040154) }
                ? "в системе нет AAC-кодера Media Foundation (Windows N без Media Feature Pack?)"
                : ex.Message;
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

    private static ISampleProvider Open(RawTrack track, List<Stream> streams, long delayFrames)
    {
        var stream = new FileStream(track.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        streams.Add(stream);
        var raw = new RawSourceWaveStream(stream, WaveFormat.CreateIeeeFloatWaveFormat(track.SampleRate, track.Channels));
        ISampleProvider provider = raw.ToSampleProvider();
        if (track.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (track.Channels > 2)
        {
            provider = new MultiplexingSampleProvider(new[] { provider }, 2);
        }
        if (track.SampleRate != MixMath.OutputSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, MixMath.OutputSampleRate);
        }
        if (delayFrames > 0)
        {
            provider = new OffsetSampleProvider(provider) { DelayBySamples = (int)(delayFrames * MixMath.OutputChannels) };
        }
        return provider;
    }

    private sealed class ClipSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        public ClipSampleProvider(ISampleProvider source) => _source = source;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var n = _source.Read(buffer, offset, count);
            for (var i = offset; i < offset + n; i++) buffer[i] = MixMath.Clip(buffer[i]);
            return n;
        }
    }
}
