using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalMeetingRecorder.Core.LLM;
using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Speakr;

namespace UniversalMeetingRecorder.Core.Tasks;

public sealed class ExtractionException : Exception
{
    public ExtractionException(string message) : base(message) { }

    public static ExtractionException TooLong(int chars) => new($"Транскрипт слишком длинный для одного запроса ({chars} символов)");
    public static ExtractionException BadResponse(string detail) => new($"LLM вернула ответ, который не удалось разобрать: {detail}");
}

/// <summary>
/// Индекс реплик для поиска цитат: нормализованный текст каждой реплики и каждой пары соседних
/// (просьба и согласие часто разнесены по соседним репликам). Сравнение — по UTF-16, как на Mac.
/// </summary>
public sealed class TranscriptIndex
{
    private readonly List<(string Text, double? StartTime)> _windows = new();

    public TranscriptIndex(IReadOnlyList<SpeakrTranscriptSegment> segments)
    {
        var normalized = segments.Select(s => ActionItemExtractor.Normalize(s.Sentence)).ToList();
        for (var i = 0; i < segments.Count; i++)
        {
            _windows.Add((normalized[i], segments[i].StartTime));
            if (i + 1 < segments.Count) _windows.Add((normalized[i] + " " + normalized[i + 1], segments[i].StartTime));
        }
    }

    /// <summary>Доля цитаты (по самой длинной общей подстроке), найденная в реплике или паре реплик.</summary>
    public (double Score, double? StartTime) Locate(string quote)
    {
        var q = ActionItemExtractor.Normalize(quote);
        if (q.Length == 0) return (0, null);
        (double Score, double? StartTime) best = (0, null);
        foreach (var w in _windows)
        {
            var score = ActionItemExtractor.LongestCommonSubstring(q, w.Text) / (double)q.Length;
            if (score > best.Score)
            {
                best = (score, w.StartTime);
                if (score >= 1) break;
            }
        }
        return best;
    }
}

/// <summary>
/// Находит в транскрипте задачи, лежащие на пользователе, и проверяет ответ LLM локально:
/// модели нельзя доверять ни цитаты, ни сроки, ни таймкоды. Порт ActionItemExtractor.swift —
/// промпт дословный, пороги те же.
/// </summary>
public sealed class ActionItemExtractor
{
    public const double QuoteThreshold = 0.8;
    public const int ChunkChars = 70_000;
    public const int ChunkOverlapChars = 6_000;
    public const int MaxChunks = 10;

    private readonly ILlmProvider _llm;

    /// <summary>Паузы между повторами (5 с, 10 с); в тестах — нулевые.</summary>
    public Func<int, TimeSpan> RetryDelay { get; init; } = attempt => TimeSpan.FromSeconds(5 * attempt);

    public ActionItemExtractor(ILlmProvider llm) => _llm = llm;

    public async Task<List<SuggestedTask>> Extract(
        Meeting meeting,
        IReadOnlyList<SpeakrTranscriptSegment> segments,
        string? notes,
        IReadOnlyList<string> myNames,
        IReadOnlyList<string> excludeTitles,
        CancellationToken ct = default)
    {
        var chunks = Chunks(segments);
        if (chunks.Count > MaxChunks) throw ExtractionException.TooLong(TranscriptText(segments).Length);

        // Куски по очереди; ошибка любого валит всю экстракцию — тихо потерять часть встречи хуже.
        var raw = new List<RawTask>();
        for (var i = 0; i < chunks.Count; i++)
        {
            (int, int)? part = chunks.Count > 1 ? (i + 1, chunks.Count) : null;
            var prompt = Prompt(meeting, TranscriptText(chunks[i]), notes, myNames, part);
            raw.AddRange(await TasksFor(prompt, ct).ConfigureAwait(false));
        }
        return Validate(raw, new TranscriptIndex(segments), excludeTitles, meeting.StartDate);
    }

    /// <summary>
    /// Запрос с повторами: невалидный JSON и временные сбои (5xx, 429, пустой ответ, сеть) повторяются
    /// до 3 раз; ошибки настройки (401, 404…) — сразу наружу.
    /// </summary>
    private async Task<List<RawTask>> TasksFor(string prompt, CancellationToken ct)
    {
        const int attempts = 3;
        Exception last = LlmException.Invalid();
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return Parse(await _llm.CompleteJson(prompt, ct).ConfigureAwait(false));
            }
            catch (ExtractionException ex)
            {
                last = ex;
            }
            catch (LlmException ex)
            {
                if (!IsTransient(ex)) throw;
                last = ex;
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                last = ex; // таймаут HttpClient, а не отмена пользователем
            }
            if (attempt < attempts) await Task.Delay(RetryDelay(attempt), ct).ConfigureAwait(false);
        }
        throw last;
    }

    private static bool IsTransient(LlmException e) => e.Kind switch
    {
        LlmErrorKind.Http => e.StatusCode >= 500 || e.StatusCode == 429,
        LlmErrorKind.EmptyContent => true,
        _ => false,
    };

    // ----- Нарезка -----

    /// <summary>Режет реплики на куски по ChunkChars, не разрывая реплику, с перекрытием ChunkOverlapChars.</summary>
    public static List<List<SpeakrTranscriptSegment>> Chunks(IReadOnlyList<SpeakrTranscriptSegment> segments)
    {
        var sizes = segments.Select(s => TranscriptLine(s).Length + 1).ToList();
        if (sizes.Sum() <= ChunkChars) return new List<List<SpeakrTranscriptSegment>> { segments.ToList() };

        var result = new List<List<SpeakrTranscriptSegment>>();
        var start = 0;
        while (start < segments.Count)
        {
            var end = start;
            var size = 0;
            while (end < segments.Count && (size + sizes[end] <= ChunkChars || end == start))
            {
                size += sizes[end];
                end++;
            }
            result.Add(segments.Skip(start).Take(end - start).ToList());
            if (end >= segments.Count) break;

            var back = end;
            var overlap = 0;
            while (back > start + 1 && overlap + sizes[back - 1] <= ChunkOverlapChars)
            {
                back--;
                overlap += sizes[back];
            }
            start = back;
        }
        return result;
    }

    // ----- Промпт -----

    public static string TranscriptText(IEnumerable<SpeakrTranscriptSegment> segments) =>
        string.Join("\n", segments.Select(TranscriptLine));

    public static string TranscriptLine(SpeakrTranscriptSegment s)
    {
        var speaker = s.Speaker ?? "?";
        if (s.StartTime is not { } start) return $"{speaker}: {s.Sentence}";
        var t = (int)start;
        return string.Format(CultureInfo.InvariantCulture, "[{0:00}:{1:00}] ", t / 60, t % 60) + $"{speaker}: {s.Sentence}";
    }

    public static string Prompt(Meeting meeting, string transcript, string? notes, IReadOnlyList<string> myNames, (int Index, int Count)? part = null)
    {
        var local = meeting.StartDate.ToLocalTime();
        var date = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var tomorrow = local.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dayName = local.ToString("dddd", CultureInfo.GetCultureInfo("ru-RU"));
        var names = string.Join(", ", myNames.Select(n => $"«{n}»"));
        var summary = notes ?? "";
        if (summary.Length > 4000) summary = summary[..4000];
        var partNote = part is { } p
            ? $"\nЭто фрагмент {p.Index} из {p.Count} одной длинной встречи. Верни задачи, обязательства по которым прозвучали в этом фрагменте; начало и конец фрагмента могут обрывать разговор — такие неполные обрывки не включай.\n"
            : "";

        var sb = new StringBuilder();
        sb.Append("Ты выделяешь задачи из транскрипта рабочей встречи.\n");
        sb.Append($"Дата встречи: {date} ({dayName}). Меня зовут (в транскрипте и в саммари): {names}. Речь распознана автоматически, с ошибками («костюм» вместо «костыль») — пиши title по смыслу.\n");
        sb.Append($"Нужны задачи, которые лежат на МНЕ: (а) я пообещал что-то сделать; (б) меня попросили и я согласился, в том числе коротким «да/окей/давай/сейчас скину» (просьба и согласие часто в соседних репликах). НЕ включай задачи других людей, шутки и то, что уже сделано в ходе встречи.{partNote}\n");
        sb.Append('\n');
        sb.Append("Саммари встречи (подсказка, источник истины — транскрипт):\n");
        sb.Append("---\n");
        sb.Append(summary).Append('\n');
        sb.Append("---\n");
        sb.Append('\n');
        sb.Append("Верни JSON {\"tasks\": [ ... ]}, каждый элемент:\n");
        sb.Append("- \"title\": действие в инфинитиве, до 12 слов;\n");
        sb.Append("- \"request_quote\": ДОСЛОВНАЯ цитата ТОЛЬКО из блока ТРАНСКРИПТ (не из саммари!) — просьба или моё обещание;\n");
        sb.Append("- \"answer_quote\": ДОСЛОВНАЯ цитата моего согласия (для случая (а) повтори ту же цитату);\n");
        sb.Append($"- \"due_date\": \"YYYY-MM-DD\" ТОЛЬКО если срок прямо назван словами; «сегодня» = {date}, «завтра» = {tomorrow}, остальные относительные сроки считай от даты встречи; нет срока — null;\n");
        sb.Append("- \"due_quote\": ДОСЛОВНАЯ цитата, где назван срок, либо null;\n");
        sb.Append("- \"confidence\": число от 0 до 1.\n");
        sb.Append('\n');
        sb.Append("ТРАНСКРИПТ:\n");
        sb.Append(transcript);
        return sb.ToString();
    }

    // ----- Разбор ответа -----

    public sealed class RawTask
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("request_quote")] public string? RequestQuote { get; set; }
        [JsonPropertyName("answer_quote")] public string? AnswerQuote { get; set; }
        [JsonPropertyName("due_date")] public string? DueDate { get; set; }
        [JsonPropertyName("due_quote")] public string? DueQuote { get; set; }
        [JsonPropertyName("confidence")] public double? Confidence { get; set; }
    }

    private sealed class Envelope
    {
        [JsonPropertyName("tasks")] public List<RawTask>? Tasks { get; set; }
    }

    /// <summary>Достаёт JSON из ответа: модели иногда оборачивают его в ```json … ``` или пишут пояснение.</summary>
    public static List<RawTask> Parse(string content)
    {
        if (TryEnvelope(content) is { } direct) return direct;
        foreach (var (open, close) in new[] { ('{', '}'), ('[', ']') })
        {
            var start = content.IndexOf(open);
            var end = content.LastIndexOf(close);
            if (start < 0 || end < 0 || start >= end) continue;
            var slice = content[start..(end + 1)];
            if (TryEnvelope(slice) is { } env) return env;
            try
            {
                if (JsonSerializer.Deserialize<List<RawTask>>(slice) is { } list) return list;
            }
            catch (JsonException)
            {
            }
        }
        throw ExtractionException.BadResponse(content.Length <= 120 ? content : content[..120]);
    }

    private static List<RawTask>? TryEnvelope(string text)
    {
        try
        {
            var env = JsonSerializer.Deserialize<Envelope>(text);
            return env?.Tasks;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ----- Валидация -----

    public static List<SuggestedTask> Validate(IEnumerable<RawTask> raw, TranscriptIndex index, IEnumerable<string> excludeTitles, DateTime meetingDateUtc)
    {
        var meetingDay = meetingDateUtc.ToLocalTime().Date;
        var seen = new HashSet<string>(excludeTitles.Select(Normalize));
        var result = new List<SuggestedTask>();

        foreach (var item in raw)
        {
            var title = (item.Title ?? "").Trim();
            var key = Normalize(title);
            if (key.Length == 0 || seen.Contains(key)) continue;
            seen.Add(key);

            // Цитату ищем по просьбе, затем по согласию — лучшая из двух.
            var quotes = new[] { item.RequestQuote, item.AnswerQuote }.Where(q => q is not null && Normalize(q).Length > 0).Select(q => q!).ToList();
            (double Score, double? StartTime) best = (0, null);
            var bestQuote = "";
            foreach (var quote in quotes)
            {
                var match = index.Locate(quote);
                if (match.Score > best.Score)
                {
                    best = match;
                    bestQuote = quote;
                }
            }
            var confirmed = best.Score >= QuoteThreshold;

            // Срок — только при найденной цитате про срок и не раньше дня встречи. Ложный дедлайн хуже отсутствующего.
            DateTime? due = null;
            if (item.DueDate is { } dueString && ParseDay(dueString) is { } date && item.DueQuote is { } dueQuote &&
                date >= meetingDay && index.Locate(dueQuote).Score >= QuoteThreshold)
            {
                due = date.ToUniversalTime();
            }

            result.Add(new SuggestedTask
            {
                Id = Guid.NewGuid(),
                Title = title,
                Quote = bestQuote.Length == 0 ? quotes.FirstOrDefault() ?? "" : bestQuote,
                Timestamp = confirmed ? best.StartTime : null,
                DueDate = due,
                Confirmed = confirmed,
                Confidence = item.Confidence,
            });
        }
        return DropDuplicates(result);
    }

    /// <summary>Дубль из зоны перекрытия кусков: та же реплика и Jaccard слов заголовков ≥ 0.4.</summary>
    public static List<SuggestedTask> DropDuplicates(List<SuggestedTask> tasks)
    {
        var kept = new List<SuggestedTask>();
        foreach (var task in tasks)
        {
            var i = kept.FindIndex(k => IsDuplicate(k, task));
            if (i >= 0)
            {
                if ((task.Confidence ?? 0) > (kept[i].Confidence ?? 0)) kept[i] = task;
            }
            else
            {
                kept.Add(task);
            }
        }
        return kept;
    }

    private static bool IsDuplicate(SuggestedTask a, SuggestedTask b)
    {
        if (a.Timestamp is not { } ta || b.Timestamp is not { } tb || ta != tb) return false;
        var wa = Normalize(a.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wb = Normalize(b.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var union = wa.Union(wb).Count();
        return union > 0 && wa.Intersect(wb).Count() / (double)union >= 0.4;
    }

    /// <summary>Локальная полночь дня «yyyy-MM-dd» (Kind = Local) или null.</summary>
    public static DateTime? ParseDay(string s) =>
        DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? DateTime.SpecifyKind(d.Date, DateTimeKind.Local)
            : null;

    // ----- Сравнение текста -----

    /// <summary>Нижний регистр, всё кроме букв и цифр — в одиночные пробелы.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder();
        var lastWasSpace = true;
        var text = s.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        foreach (var ch in text)
        {
            if (char.IsLetter(ch) || char.IsNumber(ch) || char.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    public static int LongestCommonSubstring(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        var best = 0;
        for (var i = 1; i <= a.Length; i++)
        {
            Array.Clear(current);
            for (var j = 1; j <= b.Length; j++)
            {
                if (a[i - 1] != b[j - 1]) continue;
                current[j] = previous[j - 1] + 1;
                if (current[j] > best) best = current[j];
            }
            (previous, current) = (current, previous);
        }
        return best;
    }
}
