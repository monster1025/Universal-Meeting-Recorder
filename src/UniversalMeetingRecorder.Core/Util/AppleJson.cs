using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalMeetingRecorder.Core.Util;

/// <summary>
/// Сериализация JSON в том же виде, что Swift <c>JSONEncoder</c> с <c>[.prettyPrinted, .sortedKeys]</c>
/// на macOS-версии: отступ 2 пробела, <c>"key" : value</c> (пробел до двоеточия), ключи по
/// возрастанию (ordinal), пустой массив — <c>[\n\n  ]</c>, слэш экранируется как <c>\/</c>,
/// не-ASCII пишется как есть, без завершающего перевода строки.
///
/// Байтовая идентичность — не косметика: папки встреч могут переезжать между Mac и Windows
/// (общий диск, бэкап), и тогда diff/синхронизация видят только реальные изменения.
/// Проверяется golden-тестом на настоящих meeting.json с Mac.
/// </summary>
public static class AppleJson
{
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node, int indent)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                WriteObject(sb, obj, indent);
                break;
            case JsonArray arr:
                WriteArray(sb, arr, indent);
                break;
            case JsonValue value:
                WriteValue(sb, value);
                break;
        }
    }

    private static void WriteObject(StringBuilder sb, JsonObject obj, int indent)
    {
        var keys = obj.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        sb.Append('{');
        sb.Append('\n');
        for (var i = 0; i < keys.Count; i++)
        {
            Pad(sb, indent + 1);
            WriteString(sb, keys[i]);
            sb.Append(" : ");
            Write(sb, obj[keys[i]], indent + 1);
            if (i < keys.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        Pad(sb, indent);
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, JsonArray arr, int indent)
    {
        sb.Append('[');
        sb.Append('\n');
        if (arr.Count == 0)
        {
            // Swift печатает пустой массив как "[\n\n<indent>]".
            sb.Append('\n');
        }
        for (var i = 0; i < arr.Count; i++)
        {
            Pad(sb, indent + 1);
            Write(sb, arr[i], indent + 1);
            if (i < arr.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        Pad(sb, indent);
        sb.Append(']');
    }

    private static void WriteValue(StringBuilder sb, JsonValue value)
    {
        if (value.TryGetValue<string>(out var str)) { WriteString(sb, str); return; }
        if (value.TryGetValue<bool>(out var b)) { sb.Append(b ? "true" : "false"); return; }
        if (value.TryGetValue<int>(out var i)) { sb.Append(i.ToString(CultureInfo.InvariantCulture)); return; }
        if (value.TryGetValue<long>(out var l)) { sb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
        if (value.TryGetValue<uint>(out var u)) { sb.Append(u.ToString(CultureInfo.InvariantCulture)); return; }
        if (value.TryGetValue<double>(out var d)) { sb.Append(FormatDouble(d)); return; }
        if (value.TryGetValue<JsonElement>(out var element))
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String: WriteString(sb, element.GetString()!); return;
                case JsonValueKind.True: sb.Append("true"); return;
                case JsonValueKind.False: sb.Append("false"); return;
                case JsonValueKind.Null: sb.Append("null"); return;
                case JsonValueKind.Number:
                    sb.Append(element.TryGetInt64(out var n) ? n.ToString(CultureInfo.InvariantCulture) : FormatDouble(element.GetDouble()));
                    return;
            }
        }
        sb.Append(value.ToJsonString());
    }

    /// <summary>Кратчайшее round-trip представление, как у Swift: 48000 → "48000", 1e-05 → "1e-05".</summary>
    public static string FormatDouble(double d)
    {
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.Replace("E", "e");
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '/': sb.Append("\\/"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static void Pad(StringBuilder sb, int indent) => sb.Append(' ', indent * 2);
}
