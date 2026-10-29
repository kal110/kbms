using System.Security.Cryptography;

namespace KnowledgeBase.Core.Application.Security;

/// <summary>
/// PBKDF2-HMAC-SHA512 hashes, stored as "pbkdf2-sha512$iterations$salt$hash" (base64). The iterations
/// travel with each hash, so the work factor can rise later: <see cref="Verify"/> reports older ones.
/// </summary>
public static class PasswordHasher
{
    private const string Algorithm = "pbkdf2-sha512";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>OWASP's figure for PBKDF2-HMAC-SHA512 (about a tenth of a second per check).</summary>
    public const int Iterations = 210_000;

    private static readonly Lazy<string> Decoy = new(() => Hash(Guid.NewGuid().ToString()));

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, HashBytes);
        return $"{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Whether the password matches; NeedsRehash when the hash used fewer iterations than today's.</summary>
    public static (bool Valid, bool NeedsRehash) Verify(string stored, string password)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm || !int.TryParse(parts[1], out var iterations) || iterations < 1)
            return (false, false);
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return (false, false);
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
        var valid = CryptographicOperations.FixedTimeEquals(actual, expected);
        return (valid, valid && iterations < Iterations);
    }

    /// <summary>Takes as long as a real check, so a sign-in id without a password isn't quicker to turn down.</summary>
    public static void VerifyDecoy(string password) => Verify(Decoy.Value, password);
}

/// <summary>What a new password must be: long enough, not the sign-in id, not one of the most guessed.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "passw0rd", "12345678", "123456789", "1234567890", "87654321",
        "11111111", "00000000", "qwerty123", "qwertyuiop", "iloveyou", "admin123", "administrator", "welcome1",
        "abc12345", "abcd1234", "letmein1", "changeme",
        // The placeholder in .env.example and dokploy.env.example.
        "change_me",
    };

    /// <summary>Null when the password will do, otherwise what to fix.</summary>
    public static string? Problem(string? password, string externalId)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinLength) return $"Use at least {MinLength} characters.";
        if (password.Length > MaxLength) return $"Use at most {MaxLength} characters.";
        if (string.Equals(password.Trim(), externalId, StringComparison.OrdinalIgnoreCase)) return "Don't use the sign-in id as the password.";
        if (Common.Contains(password.Trim())) return "That password is too easy to guess. Choose another.";
        return null;
    }
}
