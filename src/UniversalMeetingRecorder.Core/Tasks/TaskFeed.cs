using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace UniversalMeetingRecorder.Core.Tasks;

/// <summary>
/// Контракт локального read-only API задач — совпадает с macOS-версией (Shared/TaskFeedContract.swift):
/// порт 59731 на loopback, статический Bearer-токен, GET /tasks. Даты — секунды с 1970 (число),
/// null-поля не пишутся, только незакрытые задачи в порядке показа приложения.
/// </summary>
public static class TaskFeed
{
    public const int Port = 59731;
    public const string Token = "cr-tasks-b7d1e4c9-3a52-4f6e-9c81-5d2a0f7b9e31";
    public const string TasksPath = "/tasks";

    public static byte[] Encode(IEnumerable<TodoItem> sortedTasks, DateTime generatedAtUtc)
    {
        var arr = new JsonArray();
        foreach (var t in sortedTasks.Where(t => !t.IsCompleted))
        {
            var o = new JsonObject
            {
                ["id"] = t.Id,
                ["title"] = t.Title,
                ["scope"] = t.Scope.ToDb(),
            };
            if (t.DueDate is { } due) o["dueDate"] = Util.IsoDate.ToUnixSeconds(due);
            arr.Add(o);
        }
        var root = new JsonObject
        {
            ["generatedAt"] = Util.IsoDate.ToUnixSeconds(generatedAtUtc),
            ["tasks"] = arr,
        };
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }
}
