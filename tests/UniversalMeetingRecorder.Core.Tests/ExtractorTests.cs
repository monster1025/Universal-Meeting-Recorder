using UniversalMeetingRecorder.Core.LLM;
using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Speakr;
using UniversalMeetingRecorder.Core.Tasks;

namespace UniversalMeetingRecorder.Core.Tests;

public class ExtractorTests
{
    private sealed class FakeLlm : ILlmProvider
    {
        public Queue<Func<string>> Answers { get; } = new();
        public List<string> Prompts { get; } = new();

        public Task<string> CompleteJson(string prompt, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(Answers.Dequeue()());
        }
    }

    private static readonly List<SpeakrTranscriptSegment> Segments = new()
    {
        new("SPEAKER_00", "Иван, скинешь отчёт до пятницы?", 61, 64),
        new("SPEAKER_01", "Да, сейчас скину.", 64, 66),
        new("SPEAKER_00", "Отлично, спасибо.", 66, 67),
    };

    private static Meeting Meeting() => new()
    {
        AppName = "Zoom",
        StartDate = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Local).ToUniversalTime(),
        FolderName = "f",
    };

    [Fact]
    public void NormalizeAndLcs()
    {
        Assert.Equal("привет мир 42", ActionItemExtractor.Normalize("  Привет, МИР!! 42 "));
        Assert.Equal(3, ActionItemExtractor.LongestCommonSubstring("abcd", "xbcdy"));
    }

    [Fact]
    public void PromptMatchesSwiftTemplate()
    {
        var p = ActionItemExtractor.Prompt(Meeting(), "[01:01] A: x", "саммари", new[] { "Иван", "Иванов" }, (2, 3));
        var lines = p.Split('\n');
        Assert.Equal("Ты выделяешь задачи из транскрипта рабочей встречи.", lines[0]);
        Assert.Equal("Дата встречи: 2026-11-18 (среда). Меня зовут (в транскрипте и в саммари): «Иван», «Иванов». Речь распознана автоматически, с ошибками («костюм» вместо «костыль») — пиши title по смыслу.", lines[1]);
        Assert.EndsWith("то, что уже сделано в ходе встречи.", lines[2]);
        Assert.StartsWith("Это фрагмент 2 из 3 одной длинной встречи.", lines[3]);
        Assert.Equal("", lines[4]);
        Assert.Equal("", lines[5]);
        Assert.Equal("Саммари встречи (подсказка, источник истины — транскрипт):", lines[6]);
        Assert.Equal("саммари", lines[8]);
        Assert.Contains("«сегодня» = 2026-11-18, «завтра» = 2026-11-19,", p);
        Assert.EndsWith("ТРАНСКРИПТ:\n[01:01] A: x", p);

        var single = ActionItemExtractor.Prompt(Meeting(), "t", null, new[] { "Я" });
        Assert.Contains("сделано в ходе встречи.\n\nСаммари", single);
        Assert.Contains("---\n\n---\n", single);
    }

    [Fact]
    public void TranscriptLineFormat()
    {
        Assert.Equal("[01:01] SPEAKER_00: hi", ActionItemExtractor.TranscriptLine(new("SPEAKER_00", "hi", 61.9, null)));
        Assert.Equal("?: hi", ActionItemExtractor.TranscriptLine(new(null, "hi", null, null)));
    }

    [Fact]
    public void ChunksWithOverlap()
    {
        var segs = Enumerable.Range(0, 300).Select(i => new SpeakrTranscriptSegment("S", new string('x', 990), i, i + 1)).ToList();
        var chunks = ActionItemExtractor.Chunks(segs);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(ActionItemExtractor.TranscriptText(c).Length <= ActionItemExtractor.ChunkChars));
        // Перекрытие: начало следующего куска — внутри предыдущего.
        Assert.Contains(chunks[1][0], chunks[0]);
        Assert.Equal(segs.Last(), chunks.Last().Last());
    }

    [Fact]
    public void ParsesWrappedJson()
    {
        Assert.Single(ActionItemExtractor.Parse("```json\n{\"tasks\":[{\"title\":\"x\"}]}\n```"));
        Assert.Single(ActionItemExtractor.Parse("вот: [{\"title\":\"x\"}]"));
        Assert.Throws<ExtractionException>(() => ActionItemExtractor.Parse("нет json"));
    }

    [Fact]
    public async Task ExtractValidatesQuotesDatesAndDuplicates()
    {
        var llm = new FakeLlm();
        llm.Answers.Enqueue(() => """
            {"tasks":[
              {"title":"Скинуть отчёт","request_quote":"скинешь отчёт до пятницы","answer_quote":"Да, сейчас скину","due_date":"2026-11-20","due_quote":"до пятницы","confidence":0.9},
              {"title":"Скинуть отчёт коллеге","request_quote":"скинешь отчёт до пятницы","confidence":0.5},
              {"title":"Выдуманная","request_quote":"этого не было никогда вообще","due_date":"2026-11-10","due_quote":"этого не было","confidence":0.4},
              {"title":"Уже отклонённая","request_quote":"Отлично, спасибо"},
              {"title":"  "}
            ]}
            """);
        var extractor = new ActionItemExtractor(llm) { RetryDelay = _ => TimeSpan.Zero };
        var result = await extractor.Extract(Meeting(), Segments, null, new[] { "Иван" }, new[] { "уже  отклонённая" });
        Assert.Equal(2, result.Count);
        var first = result[0];
        Assert.Equal("Скинуть отчёт", first.Title);
        Assert.True(first.Confirmed);
        Assert.Equal(61, first.Timestamp);
        Assert.Equal(new DateTime(2026, 11, 20), first.DueDate!.Value.ToLocalTime().Date);
        var fake = result[1];
        Assert.False(fake.Confirmed);
        Assert.Null(fake.Timestamp);
        Assert.Null(fake.DueDate);
    }

    [Fact]
    public async Task RetriesTransientErrorsOnly()
    {
        var llm = new FakeLlm();
        llm.Answers.Enqueue(() => throw LlmException.Http(502, "bad gateway"));
        llm.Answers.Enqueue(() => "мусор");
        llm.Answers.Enqueue(() => "{\"tasks\":[]}");
        var extractor = new ActionItemExtractor(llm) { RetryDelay = _ => TimeSpan.Zero };
        Assert.Empty(await extractor.Extract(Meeting(), Segments, null, new[] { "Я" }, Array.Empty<string>()));
        Assert.Equal(3, llm.Prompts.Count);

        var llm401 = new FakeLlm();
        llm401.Answers.Enqueue(() => throw LlmException.Http(401, "no"));
        var e = new ActionItemExtractor(llm401) { RetryDelay = _ => TimeSpan.Zero };
        await Assert.ThrowsAsync<LlmException>(() => e.Extract(Meeting(), Segments, null, new[] { "Я" }, Array.Empty<string>()));
        Assert.Single(llm401.Prompts);
    }

    [Fact]
    public void LlmClientUrlNormalization()
    {
        Assert.NotNull(OpenAiCompatibleClient.Create("http://host:4000/v1/", "k", "m"));
        Assert.Null(OpenAiCompatibleClient.Create("host", "k", "m"));
        Assert.Null(OpenAiCompatibleClient.Create("http://host", "", "m"));
        Assert.Null(OpenAiCompatibleClient.Create("http://host", "k", " "));
    }

    [Fact]
    public async Task LlmClientRequestShape()
    {
        string? body = null;
        string? path = null;
        var handler = new FakeHandler(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().Result;
            return FakeHandler.Json("""{"choices":[{"message":{"content":"{\"tasks\":[]}","reasoning_content":"x"}}]}""");
        });
        var client = OpenAiCompatibleClient.Create("http://host:4000/v1", "key", "model", handler)!;
        Assert.Equal("{\"tasks\":[]}", await client.CompleteJson("p"));
        Assert.Equal("/v1/chat/completions", path);
        Assert.Contains("\"response_format\":{\"type\":\"json_object\"}", body);
        Assert.Contains("\"temperature\":0", body);
    }
}
