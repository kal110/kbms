using System.Text.RegularExpressions;
using KnowledgeBase.Core.Application.Ai;

namespace KnowledgeBase.Core.Infrastructure.Ai;

/// <summary>
/// The keyword half of hybrid search (frame 10, step 5): BM25 term weights as a sparse vector.
/// Terms are hashed to ids (FNV-1a), documents carry saturated term frequencies, and Qdrant applies
/// IDF itself (the collection's sparse vector uses modifier "idf"), so no vocabulary is stored.
/// Exact tokens such as "CS301", "AP-2024-017" parts and years match even when the dense model
/// would blur them.
/// </summary>
public static partial class SparseEncoder
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>Typical chunk length in terms, for length normalization.</summary>
    private const double AverageLength = 220;

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "has", "have", "in", "is", "it", "its",
        "of", "on", "or", "that", "the", "this", "to", "was", "were", "will", "with", "which", "what", "who",
        "how", "when", "where", "why", "do", "does", "did", "can", "may", "should", "shall", "must", "not",
        "ang", "ng", "sa", "mga", "na", "ay",
    ];

    public static SparseVector EncodeDocument(string text)
    {
        var terms = Terms(text).ToList();
        var weights = new Dictionary<uint, float>();
        foreach (var group in terms.GroupBy(t => t))
        {
            var tf = group.Count();
            var weight = tf * (K1 + 1) / (tf + K1 * (1 - B + B * terms.Count / AverageLength));
            var id = Hash(group.Key);
            weights[id] = weights.GetValueOrDefault(id) + (float)weight;
        }
        return ToVector(weights);
    }

    /// <summary>A query term counts once; IDF on the server does the weighting.</summary>
    public static SparseVector EncodeQuery(string text) =>
        ToVector(Terms(text).Distinct().GroupBy(Hash).ToDictionary(g => g.Key, _ => 1f));

    public static IEnumerable<string> Terms(string text) =>
        Term().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => (t.Length > 1 || char.IsDigit(t[0])) && !StopWords.Contains(t));

    private static SparseVector ToVector(Dictionary<uint, float> weights)
    {
        var ordered = weights.OrderBy(w => w.Key).ToList();
        return new SparseVector(ordered.Select(w => w.Key).ToArray(), ordered.Select(w => w.Value).ToArray());
    }

    /// <summary>FNV-1a, 32-bit: stable across processes and versions, unlike string.GetHashCode.</summary>
    public static uint Hash(string term)
    {
        var hash = 2166136261u;
        foreach (var c in term)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Term();
}
