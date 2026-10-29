using KnowledgeBase.Core.Common;

namespace KnowledgeBase.Core.Application.Settings;

/// <summary>
/// Collects field errors under the camelCase paths the SPA sends ("items[2].files"), then throws
/// one 400 with all of them.
/// </summary>
internal sealed class FieldErrors
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public bool Any => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(message);
    }

    /// <summary>Adds the message when <paramref name="ok"/> is false.</summary>
    public void Check(bool ok, string field, string message)
    {
        if (!ok) Add(field, message);
    }

    public void ThrowIfAny()
    {
        if (Any) throw new ValidationAppException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
    }
}
