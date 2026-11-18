using System.Globalization;
using UniversalMeetingRecorder.Core.Speakr;

namespace UniversalMeetingRecorder.Core.Transcription;

/// <summary>Speakr за общим интерфейсом сервисов транскрипции. Поведение — ровно то, что было в пайплайне.</summary>
public sealed class SpeakrBackend : ITranscriptionBackend
{
    private readonly SpeakrClient _client;

    public SpeakrBackend(SpeakrClient client) => _client = client;

    public string Kind => TranscriptionBackends.Speakr;

    public async Task<string> Upload(string filePath, CancellationToken ct) =>
        (await _client.Upload(filePath, ct).ConfigureAwait(false)).Id.ToString(CultureInfo.InvariantCulture);

    public async Task<RemoteStatus> GetStatus(string remoteId, CancellationToken ct)
    {
        var status = await _client.Status(Id(remoteId), ct).ConfigureAwait(false);
        var state = status.Status switch
        {
            SpeakrRemoteStatus.Completed => RemoteState.Completed,
            SpeakrRemoteStatus.Failed => RemoteState.Failed,
            _ => RemoteState.Pending, // PENDING / QUEUED / PROCESSING / SUMMARIZING
        };
        return new RemoteStatus(state, status.Status, status.ErrorMessage);
    }

    public async Task<RemoteResult> GetResult(string remoteId, CancellationToken ct)
    {
        var id = Id(remoteId);
        var transcript = await _client.TranscriptRaw(id, ct).ConfigureAwait(false);
        var summary = await _client.Summary(id, ct).ConfigureAwait(false);
        // Заголовок — строго до возможного удаления записи; не критичен.
        string? title = null;
        try
        {
            title = (await _client.Recording(id, ct).ConfigureAwait(false)).Title?.Trim();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }
        return new RemoteResult(transcript, summary.Summary ?? "", string.IsNullOrEmpty(title) ? null : title);
    }

    public Task Delete(string remoteId, CancellationToken ct) => _client.Delete(Id(remoteId), ct);

    private static int Id(string remoteId) => int.Parse(remoteId, CultureInfo.InvariantCulture);
}
