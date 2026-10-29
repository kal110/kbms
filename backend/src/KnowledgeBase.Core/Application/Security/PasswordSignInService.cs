using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Settings;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Security;

/// <summary>NewPassword answers "password-change-required": the password was a temporary one.</summary>
public sealed record PasswordSignInRequest(string Login, string Password, string? NewPassword);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Who signed in; the API host signs the token for them.</summary>
public sealed record SignedInUser(string ExternalId, string DisplayName, string Initials, string? Email, string? Title);

/// <summary>
/// Password sign-in, while there is no identity provider (Auth:Authority empty). An administrator
/// sets a temporary password (<see cref="Org.UserAdminService.SetPasswordAsync"/>), and its holder
/// chooses their own at the first sign-in. Wrong passwords lock the account for a while
/// (<see cref="UserCredential.RecordFailedSignIn"/>); the API also limits attempts per address.
/// </summary>
public sealed class PasswordSignInService(KbDbContext db, ICurrentUser currentUser, IAuditWriter audit, TimeProvider clock)
{
    /// <summary>The same answer for an unknown id, an account without a password and a wrong password.</summary>
    private static SignInException Incorrect() =>
        new("sign-in-failed", "The sign-in id or password is incorrect.", 401);

    private static SignInException Locked(UserCredential credential, DateTimeOffset now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((credential.LockedUntil!.Value - now).TotalMinutes));
        return new SignInException("sign-in-locked", $"Too many wrong passwords. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.", 429,
            "An administrator can also set a new password for this account.");
    }

    public async Task<SignedInUser> SignInAsync(PasswordSignInRequest request, CancellationToken ct = default)
    {
        var login = request.Login?.Trim() ?? "";
        var password = request.Password ?? "";
        var errors = new FieldErrors();
        errors.Check(login.Length is > 0 and <= 320, "login", "Enter your sign-in id or email.");
        errors.Check(password.Length is > 0 and <= PasswordPolicy.MaxLength, "password", "Enter your password.");
        errors.ThrowIfAny();

        var now = clock.GetUtcNow();
        var user = await FindAsync(login, ct);
        if (user?.Credential is not { } credential)
        {
            PasswordHasher.VerifyDecoy(password);
            audit.Record("auth.sign-in-failed", "user", user?.Id, metadata: new { login, reason = user is null ? "unknown" : "no-password" });
            await db.SaveChangesAsync(ct);
            throw Incorrect();
        }
        if (credential.IsLocked(now)) throw Locked(credential, now);

        var (valid, needsRehash) = PasswordHasher.Verify(credential.PasswordHash, password);
        if (!valid)
        {
            credential.RecordFailedSignIn(now);
            audit.Record("auth.sign-in-failed", "user", user.Id, metadata: new { login, reason = "wrong-password", locked = credential.IsLocked(now) });
            await db.SaveChangesAsync(ct);
            throw credential.IsLocked(now) ? Locked(credential, now) : Incorrect();
        }

        credential.RecordSignIn();
        if (credential.MustChangePassword)
        {
            if (request.NewPassword is null)
            {
                await db.SaveChangesAsync(ct);
                throw new BusinessRuleException("password-change-required", "Choose a new password to continue.",
                    "This password was set by an administrator for your first sign-in.");
            }
            ValidateNewPassword(request.NewPassword, password, user.ExternalId);
            credential.SetPassword(PasswordHasher.Hash(request.NewPassword), mustChange: false, now);
            audit.Record("auth.password-changed", "user", user.Id, actorUserId: user.Id);
        }
        else if (needsRehash)
        {
            credential.PasswordHash = PasswordHasher.Hash(password);
        }

        audit.Record("auth.signed-in", "user", user.Id, actorUserId: user.Id);
        await db.SaveChangesAsync(ct);
        return SignedIn(user);
    }

    /// <summary>The signed-in user's own change. Their other sessions end; the API issues this one a new token.</summary>
    public async Task<SignedInUser> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var user = await db.Users.Include(u => u.Credential).SingleAsync(u => u.Id == me.UserId, ct);
        var credential = user.Credential
            ?? throw new BusinessRuleException("no-password", "This account doesn't sign in with a password.");

        var now = clock.GetUtcNow();
        if (credential.IsLocked(now)) throw Locked(credential, now);
        var current = request.CurrentPassword ?? "";
        if (!PasswordHasher.Verify(credential.PasswordHash, current).Valid)
        {
            credential.RecordFailedSignIn(now);
            await db.SaveChangesAsync(ct);
            if (credential.IsLocked(now)) throw Locked(credential, now);
            throw new ValidationAppException("currentPassword", "That isn't your current password.");
        }

        ValidateNewPassword(request.NewPassword, current, user.ExternalId);
        credential.SetPassword(PasswordHasher.Hash(request.NewPassword), mustChange: false, now);
        audit.Record("auth.password-changed", "user", user.Id);
        await db.SaveChangesAsync(ct);
        return SignedIn(user);
    }

    private static void ValidateNewPassword(string? newPassword, string currentPassword, string externalId)
    {
        var problem = PasswordPolicy.Problem(newPassword, externalId)
            ?? (newPassword == currentPassword ? "Choose a password different from the current one." : null);
        if (problem is not null) throw new ValidationAppException("newPassword", problem);
    }

    /// <summary>
    /// The sign-in id (exactly, then ignoring case), or an email address that only one active person
    /// has. Ignoring case only counts when it picks out a single person.
    /// </summary>
    private async Task<User?> FindAsync(string login, CancellationToken ct)
    {
        var active = db.Users.Include(u => u.Credential).Where(u => u.IsActive);
        if (await active.SingleOrDefaultAsync(u => u.ExternalId == login, ct) is { } exact) return exact;

        var lower = login.ToLowerInvariant();
        var matches = await active.Where(u => u.ExternalId.ToLower() == lower).Take(2).ToListAsync(ct);
        if (matches.Count == 0 && login.Contains('@'))
            matches = await active.Where(u => u.Email != null && u.Email.ToLower() == lower).Take(2).ToListAsync(ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    private static SignedInUser SignedIn(User user) => new(user.ExternalId, user.DisplayName, user.Initials, user.Email, user.Title);
}
