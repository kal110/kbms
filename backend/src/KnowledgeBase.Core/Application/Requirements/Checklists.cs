using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Domain.Requirements;

namespace KnowledgeBase.Core.Application.Requirements;

/// <summary>A non-draft set considered for a submitter's checklist.</summary>
public sealed record SetCandidate(Guid SetId, RequirementScope Scope, Guid ScopeId, int SetVersion, DateOnly? EffectiveFrom);

/// <summary>One set's slots, in the set's order.</summary>
public sealed record ChecklistLayer(RequirementScope Scope, Guid ScopeId, Guid SetId, IReadOnlyList<SubmissionRequirement> Items);

/// <summary>The rules that turn requirement sets into a checklist (pure, so they are unit-tested).</summary>
public static class Checklists
{
    /// <summary>
    /// Per scope, the set in force for a term starting on <paramref name="termStart"/>: the highest
    /// version whose effective term starts on or before it (no effective term = always). Superseded
    /// sets still count, so publishing v4 for next term leaves v3 in force for this one.
    /// </summary>
    public static IReadOnlyList<SetCandidate> PickInForce(IEnumerable<SetCandidate> candidates, DateOnly termStart) =>
        candidates
            .Where(c => c.EffectiveFrom is null || c.EffectiveFrom <= termStart)
            .GroupBy(c => (c.Scope, c.ScopeId))
            .Select(g => g.MaxBy(c => c.SetVersion)!)
            .OrderBy(c => RequirementPolicy.Rank(c.Scope))
            .ToList();

    /// <summary>
    /// Adds the layers up broadest first (college → department → program). A slot key that a more
    /// specific scope repeats is replaced in place by that scope's version of the slot.
    /// </summary>
    public static IReadOnlyList<EffectiveRequirement> Merge(IEnumerable<ChecklistLayer> layers)
    {
        var result = new List<EffectiveRequirement>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var layer in layers.OrderBy(l => RequirementPolicy.Rank(l.Scope)))
        {
            foreach (var item in layer.Items.OrderBy(i => i.SortOrder))
            {
                var effective = new EffectiveRequirement(item.Id, layer.SetId, layer.Scope, layer.ScopeId, item.Key, item.Label,
                    item.DocumentTypeId, item.IsRequired, item.MinFiles, item.MaxFiles, item.DueOn);
                if (positions.TryGetValue(item.Key, out var at))
                {
                    result[at] = effective;
                }
                else
                {
                    positions[item.Key] = result.Count;
                    result.Add(effective);
                }
            }
        }
        return result;
    }

    /// <summary>"Faculty load assignment" → "faculty_load_assignment" (same as the SPA's keyFromLabel).</summary>
    public static string KeyFromLabel(string label)
    {
        var chars = label.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        var key = string.Join('_', new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (key.Length > 0 && char.IsAsciiDigit(key[0])) key = "k_" + key;
        return key.Length > 60 ? key[..60].TrimEnd('_') : key;
    }

    /// <summary>Optional slots allow zero files; required slots need at least one.</summary>
    public static (int Min, int Max) NormalizeFiles(RequirementRule rule, FileLimit files) =>
        rule == RequirementRule.Optional ? (0, files.Max) : (files.Min, files.Max);
}
