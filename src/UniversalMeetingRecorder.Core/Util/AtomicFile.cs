using System.Text;

namespace UniversalMeetingRecorder.Core.Util;

/// <summary>Запись через временный файл + замену — аналог <c>Data.write(options: .atomic)</c>.</summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, Utf8NoBom.GetBytes(text));

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    public static Encoding Utf8 => Utf8NoBom;
}
