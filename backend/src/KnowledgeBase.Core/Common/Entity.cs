namespace KnowledgeBase.Core.Common;

/// <summary>UUIDv7 keys everywhere: time-ordered and generated before insert (frame 11).</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}

/// <summary>CreatedAt/UpdatedAt are stamped by the DbContext on save.</summary>
public interface IHasTimestamps
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Optimistic concurrency on PostgreSQL's xmin system column. The API exposes it as the ETag and
/// checks If-Match against it (412 on mismatch).
/// </summary>
public interface IConcurrencyVersioned
{
    uint Version { get; set; }
}

public static class ETags
{
    public static string From(uint version) => $"\"{version}\"";

    /// <summary>Parses an If-Match value such as "\"1234\"" or "W/\"1234\"". Null when absent or "*".</summary>
    public static uint? Parse(string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch) || ifMatch.Trim() == "*") return null;
        var value = ifMatch.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal)) value = value[2..];
        value = value.Trim('"');
        return uint.TryParse(value, out var v) ? v : throw new PreconditionFailedException("If-Match is not a valid ETag.");
    }

    /// <summary>Throws 412 when an If-Match was sent and doesn't match the current version.</summary>
    public static void Check(uint? expected, IConcurrencyVersioned entity)
    {
        if (expected is { } e && e != entity.Version) throw new PreconditionFailedException();
    }
}
