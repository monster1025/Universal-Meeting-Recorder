using NAudio.Dsp;

namespace UniversalMeetingRecorder.App.Recorder;

/// <summary>
/// Потоковое приведение interleaved float32 к фиксированному формату дорожки (частота/каналы).
/// Нужно потому, что формат сырого файла фиксируется ПЕРВЫМ буфером и сохраняется в meeting.json
/// (иначе файл не свести), а устройство посреди записи может смениться на другое — с другой
/// частотой или числом каналов. Новые данные приводятся к уже записанному формату.
/// </summary>
internal sealed class FormatAdapter
{
    private readonly int _inRate;
    private readonly int _inChannels;
    private readonly int _outRate;
    private readonly int _outChannels;
    private readonly WdlResampler? _resampler;

    public FormatAdapter(int inRate, int inChannels, int outRate, int outChannels)
    {
        _inRate = inRate;
        _inChannels = inChannels;
        _outRate = outRate;
        _outChannels = outChannels;
        if (inRate != outRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(inRate, outRate);
        }
    }

    public bool IsIdentity => _inRate == _outRate && _inChannels == _outChannels;

    /// <summary>Возвращает новый буфер в выходном формате.</summary>
    public float[] Convert(float[] input, int frames)
    {
        var mapped = MapChannels(input, frames, _inChannels, _outChannels);
        if (_resampler is null) return mapped;

        var outFramesMax = (int)((long)frames * _outRate / _inRate) + 64;
        var inNeeded = _resampler.ResamplePrepare(frames, _outChannels, out var inBuffer, out var inOffset);
        Array.Copy(mapped, 0, inBuffer, inOffset, Math.Min(inNeeded, frames) * _outChannels);
        var output = new float[outFramesMax * _outChannels];
        var produced = _resampler.ResampleOut(output, 0, Math.Min(inNeeded, frames), outFramesMax, _outChannels);
        if (produced * _outChannels == output.Length) return output;
        var trimmed = new float[produced * _outChannels];
        Array.Copy(output, trimmed, trimmed.Length);
        return trimmed;
    }

    /// <summary>
    /// Каналы: 1→2 дублирование; 2→1 среднее; N>2→2 — FL/FR плюс центр и тылы с −3 дБ (порядок
    /// каналов WAVEFORMATEXTENSIBLE: FL, FR, FC, LFE, BL, BR, …); N→1 — среднее.
    /// </summary>
    public static float[] MapChannels(float[] input, int frames, int inCh, int outCh)
    {
        if (inCh == outCh)
        {
            if (input.Length == frames * inCh) return input;
            var copy = new float[frames * inCh];
            Array.Copy(input, copy, copy.Length);
            return copy;
        }
        var output = new float[frames * outCh];
        const float minus3dB = 0.70710677f;
        for (var f = 0; f < frames; f++)
        {
            var src = f * inCh;
            if (outCh == 1)
            {
                float sum = 0;
                for (var c = 0; c < inCh; c++) sum += input[src + c];
                output[f] = sum / inCh;
            }
            else if (inCh == 1)
            {
                for (var c = 0; c < outCh; c++) output[f * outCh + c] = input[src];
            }
            else
            {
                var l = input[src];
                var r = input[src + 1];
                if (inCh >= 3)
                {
                    l += input[src + 2] * minus3dB;
                    r += input[src + 2] * minus3dB;
                }
                if (inCh >= 6)
                {
                    l += input[src + 4] * minus3dB;
                    r += input[src + 5] * minus3dB;
                }
                output[f * outCh] = l;
                if (outCh > 1) output[f * outCh + 1] = r;
            }
        }
        return output;
    }
}
