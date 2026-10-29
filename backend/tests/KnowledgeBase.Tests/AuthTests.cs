using System.Security.Cryptography;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Domain.Org;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Tests;

/// <summary>Password sign-in: hashes, the password rules, the lockout and the credentials table.</summary>
public class AuthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 9, 30, 15, 250, TimeSpan.Zero);

    [Fact]
    public void Hash_verifies_only_the_same_password()
    {
        var hash = PasswordHasher.Hash("correct horse battery");

        Assert.StartsWith($"pbkdf2-sha512${PasswordHasher.Iterations}$", hash);
        Assert.Equal((true, false), PasswordHasher.Verify(hash, "correct horse battery"));
        Assert.False(PasswordHasher.Verify(hash, "correct horse batterY").Valid);
        Assert.False(PasswordHasher.Verify(hash, "").Valid);
    }

    [Fact]
    public void Same_password_gets_a_new_salt_each_time()
    {
        Assert.NotEqual(PasswordHasher.Hash("same password"), PasswordHasher.Hash("same password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-text")]
    [InlineData("bcrypt$10$abc$def")]
    [InlineData("pbkdf2-sha512$x$abc$def")]
    [InlineData("pbkdf2-sha512$1000$not base64!$def")]
    public void Unreadable_hash_never_verifies(string stored)
    {
        Assert.Equal((false, false), PasswordHasher.Verify(stored, "anything"));
    }

    [Fact]
    public void Hash_with_fewer_iterations_verifies_and_asks_for_a_rehash()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var bytes = Rfc2898DeriveBytes.Pbkdf2("old password", salt, 1_000, HashAlgorithmName.SHA512, 32);
        var old = $"pbkdf2-sha512$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(bytes)}";

        Assert.Equal((true, true), PasswordHasher.Verify(old, "old password"));
        Assert.Equal((false, false), PasswordHasher.Verify(old, "wrong password"));
    }

    [Theory]
    [InlineData("short1", "Use at least 8 characters.")]
    [InlineData("        ", "Use at least 8 characters.")]
    [InlineData("ana.lim2026", "Don't use the sign-in id as the password.")]
    [InlineData("ANA.LIM2026", "Don't use the sign-in id as the password.")]
    [InlineData("Password123", "That password is too easy to guess. Choose another.")]
    [InlineData("12345678", "That password is too easy to guess. Choose another.")]
    [InlineData("CHANGE_ME", "That password is too easy to guess. Choose another.")]
    public void Weak_passwords_are_refused(string password, string problem)
    {
        Assert.Equal(problem, PasswordPolicy.Problem(password, "ana.lim2026"));
    }

    [Fact]
    public void Password_length_has_a_ceiling_and_a_good_one_passes()
    {
        Assert.Equal("Use at most 128 characters.", PasswordPolicy.Problem(new string('a', 129), "ana"));
        Assert.Null(PasswordPolicy.Problem("tall-river-quiet-lamp", "ana"));
        Assert.Null(PasswordPolicy.Problem(new string('a', 128), "ana"));
    }

    [Fact]
    public void Fifth_wrong_password_in_a_row_locks_for_fifteen_minutes()
    {
        var credential = new UserCredential { PasswordHash = "x" };
        for (var i = 0; i < UserCredential.MaxFailedSignIns - 1; i++) credential.RecordFailedSignIn(Now);
        Assert.False(credential.IsLocked(Now));
        Assert.Equal(4, credential.FailedSignIns);

        credential.RecordFailedSignIn(Now);

        Assert.True(credential.IsLocked(Now));
        Assert.True(credential.IsLocked(Now.AddMinutes(14)));
        Assert.False(credential.IsLocked(Now.AddMinutes(15)));
        Assert.Equal(0, credential.FailedSignIns);
    }

    [Fact]
    public void Right_password_clears_the_count()
    {
        var credential = new UserCredential { PasswordHash = "x" };
        credential.RecordFailedSignIn(Now);
        credential.RecordFailedSignIn(Now);

        credential.RecordSignIn();

        Assert.Equal(0, credential.FailedSignIns);
        Assert.Null(credential.LockedUntil);
    }

    [Fact]
    public void New_password_unlocks_and_ends_earlier_sessions_to_the_second()
    {
        var credential = new UserCredential { PasswordHash = "x" };
        for (var i = 0; i < UserCredential.MaxFailedSignIns; i++) credential.RecordFailedSignIn(Now);

        credential.SetPassword("new-hash", mustChange: true, Now);

        Assert.Equal("new-hash", credential.PasswordHash);
        Assert.True(credential.MustChangePassword);
        Assert.False(credential.IsLocked(Now));
        Assert.Equal(Now, credential.ChangedAt);
        // A token's iat is whole seconds: one issued in this same second still counts.
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 9, 30, 15, TimeSpan.Zero), credential.SessionsValidFrom);
    }

    [Fact]
    public void Credentials_live_in_their_own_table_and_go_with_the_user()
    {
        using var db = ModelTests.NewContext();
        var sql = db.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE user_credentials", sql);
        Assert.Contains("REFERENCES users (id) ON DELETE CASCADE", sql);
        Assert.DoesNotContain("password_hash", sql[..sql.IndexOf("CREATE TABLE user_credentials", StringComparison.Ordinal)]);
    }
}
