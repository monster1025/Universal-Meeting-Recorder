namespace UniversalMeetingRecorder.Core.Settings;

/// <summary>Хранилище секретов (на Windows — Credential Manager, аналог Keychain на Mac).</summary>
public interface ISecretStore
{
    string? Get(string service, string account);
    void Set(string service, string account, string secret);
    void Delete(string service, string account);
}

/// <summary>Секреты в памяти — для тестов.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _items = new();
    public string? Get(string service, string account) => _items.GetValueOrDefault(service + "|" + account);
    public void Set(string service, string account, string secret) => _items[service + "|" + account] = secret;
    public void Delete(string service, string account) => _items.Remove(service + "|" + account);
}
