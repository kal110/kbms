using System.Text.RegularExpressions;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Requirements;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Settings;

/// <summary>Settings → Document types (frame 17). Reads are open to everyone; changes are admin-only.</summary>
public sealed partial class DocumentTypeService(KbDbContext db, ICurrentUser currentUser, IAuditWriter audit, ScopeDirectory scopes)
{
    [GeneratedRegex("^[A-Z][A-Z0-9]{0,11}$")]
    private static partial Regex CodeFormat();

    /// <summary>Literal characters plus {yyyy}, {yy} and {seq:N}, e.g. "SYL-{yyyy}-{seq:4}".</summary>
    [GeneratedRegex(@"^(?:[A-Z0-9\-_/.]|\{yyyy\}|\{yy\}|\{seq:[1-9]\})+$")]
    private static partial Regex PatternFormat();

    /// <summary>Types for pickers. Inactive (retired) types only for admins who ask for them.</summary>
    public async Task<IReadOnlyList<DocumentTypeDto>> ListAsync(bool includeInactive, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var all = includeInactive && me.IsAdmin;
        return await db.DocumentTypes.AsNoTracking()
            .Where(t => all || t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new DocumentTypeDto(t.Id, t.Code, t.Name, t.Description, t.AcademicTag, t.Scope, t.IsActive, t.ReplacedByTypeId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DocumentTypeAdminDto>> ListForAdminAsync(CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var types = await db.DocumentTypes.AsNoTracking().Include(t => t.RetentionPolicy)
            .OrderByDescending(t => t.IsActive).ThenBy(t => t.Name)
            .ToListAsync(ct);
        return await ToAdminDtosAsync(types, ct);
    }

    public async Task<DocumentTypeAdminDto> GetForAdminAsync(Guid id, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var type = await db.DocumentTypes.AsNoTracking().Include(t => t.RetentionPolicy).SingleOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Document type", id);
        return (await ToAdminDtosAsync([type], ct))[0];
    }

    public async Task<IReadOnlyList<RetentionPolicyDto>> ListRetentionPoliciesAsync(CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        return await db.RetentionPolicies.AsNoTracking()
            .OrderBy(p => p.Trigger).ThenBy(p => p.RetainYears)
            .Select(p => new RetentionPolicyDto(p.Id, p.Name, p.RetainYears, p.Trigger, p.ActionAfter))
            .ToListAsync(ct);
    }

    public async Task<DocumentTypeAdminDto> CreateAsync(CreateDocumentTypeRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();

        var code = Normalize(request.Code)?.ToUpperInvariant() ?? "";
        var errors = new FieldErrors();
        ValidateCode(code, errors);
        ValidateName(request.Name, errors);
        ValidatePattern(Normalize(request.NumberPattern), errors);
        errors.Check((request.Description?.Length ?? 0) <= 500, "description", "Use at most 500 characters.");
        errors.ThrowIfAny();

        if (await db.DocumentTypes.AnyAsync(t => t.Code == code, ct))
            throw new ConflictException("code-taken", "Another document type already uses this code.").With("code", code);
        await EnsureRetentionPolicyAsync(request.RetentionPolicyId, ct);

        var type = new DocumentType
        {
            Code = code,
            Name = request.Name.Trim(),
            Description = Normalize(request.Description),
            NumberPattern = Normalize(request.NumberPattern),
            AcademicTag = request.Tag,
            Scope = request.Scope,
            RetentionPolicyId = request.RetentionPolicyId,
        };
        db.DocumentTypes.Add(type);
        audit.Record("document-type.created", "document-type", type.Id, metadata: new { type.Code, type.Name, Tag = type.AcademicTag, type.Scope });
        await db.SaveChangesAsync(ct);
        return await GetForAdminAsync(type.Id, ct);
    }

    /// <summary>Code and tag are locked once any document uses the type (409 code-locked / tag-locked).</summary>
    public async Task<DocumentTypeAdminDto> UpdateAsync(Guid id, uint? expectedVersion, UpdateDocumentTypeRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var type = await db.DocumentTypes.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Document type", id);
        ETags.Check(expectedVersion, type);

        var errors = new FieldErrors();
        var code = request.Code is null ? null : Normalize(request.Code)?.ToUpperInvariant() ?? "";
        if (code is not null) ValidateCode(code, errors);
        if (request.Name is not null) ValidateName(request.Name, errors);
        if (request.NumberPattern is not null) ValidatePattern(Normalize(request.NumberPattern), errors);
        errors.Check((request.Description?.Length ?? 0) <= 500, "description", "Use at most 500 characters.");
        errors.ThrowIfAny();

        var changed = new List<string>();
        var inUse = await db.Documents.AnyAsync(d => d.DocumentTypeId == id, ct);
        if (code is not null && code != type.Code)
        {
            if (inUse) throw new ConflictException("code-locked", "The code can't change once documents use this type.");
            if (await db.DocumentTypes.AnyAsync(t => t.Code == code && t.Id != id, ct))
                throw new ConflictException("code-taken", "Another document type already uses this code.").With("code", code);
            type.Code = code;
            changed.Add("code");
        }
        if (request.Tag is { } tag && tag != type.AcademicTag)
        {
            // Existing documents carry the year/term the old tag asked for.
            if (inUse) throw new ConflictException("tag-locked", "The academic tag can't change once documents use this type.");
            type.AcademicTag = tag;
            changed.Add("tag");
        }
        if (request.Name is not null && request.Name.Trim() != type.Name) { type.Name = request.Name.Trim(); changed.Add("name"); }
        if (request.Description is not null) { type.Description = Normalize(request.Description); changed.Add("description"); }
        if (request.NumberPattern is not null) { type.NumberPattern = Normalize(request.NumberPattern); changed.Add("numberPattern"); }
        if (request.Scope is { } scope && scope != type.Scope) { type.Scope = scope; changed.Add("scope"); }
        if (request.ClearRetentionPolicy == true)
        {
            type.RetentionPolicyId = null;
            changed.Add("retention");
        }
        else if (request.RetentionPolicyId is { } policyId && policyId != type.RetentionPolicyId)
        {
            await EnsureRetentionPolicyAsync(policyId, ct);
            type.RetentionPolicyId = policyId;
            changed.Add("retention");
        }

        if (changed.Count > 0)
        {
            audit.Record("document-type.updated", "document-type", type.Id, metadata: new { type.Code, Changed = changed });
            await db.SaveChangesAsync(ct);
        }
        return await GetForAdminAsync(id, ct);
    }

    /// <summary>Retired types stay on existing documents but can't be picked for new ones.</summary>
    public async Task<DocumentTypeAdminDto> RetireAsync(Guid id, uint? expectedVersion, RetireDocumentTypeRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var type = await db.DocumentTypes.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Document type", id);
        ETags.Check(expectedVersion, type);
        if (!type.IsActive) throw new ConflictException("already-retired", "This document type is already retired.");

        if (request.ReplacedByTypeId is { } replacementId)
        {
            if (replacementId == id) throw new ValidationAppException("replacedByTypeId", "A type can't replace itself.");
            var replacementActive = await db.DocumentTypes.Where(t => t.Id == replacementId).Select(t => (bool?)t.IsActive).SingleOrDefaultAsync(ct);
            if (replacementActive is null) throw new ValidationAppException("replacedByTypeId", "Pick an existing document type.");
            if (replacementActive == false) throw new ValidationAppException("replacedByTypeId", "The replacement type is retired too.");
        }

        type.IsActive = false;
        type.ReplacedByTypeId = request.ReplacedByTypeId;
        audit.Record("document-type.retired", "document-type", type.Id, metadata: new { type.Code, request.ReplacedByTypeId });
        await db.SaveChangesAsync(ct);
        return await GetForAdminAsync(id, ct);
    }

    public async Task<DocumentTypeAdminDto> ReactivateAsync(Guid id, uint? expectedVersion, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var type = await db.DocumentTypes.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Document type", id);
        ETags.Check(expectedVersion, type);
        if (type.IsActive) throw new ConflictException("already-active", "This document type is already active.");

        type.IsActive = true;
        type.ReplacedByTypeId = null;
        audit.Record("document-type.reactivated", "document-type", type.Id, metadata: new { type.Code });
        await db.SaveChangesAsync(ct);
        return await GetForAdminAsync(id, ct);
    }

    private async Task<IReadOnlyList<DocumentTypeAdminDto>> ToAdminDtosAsync(IReadOnlyList<DocumentType> types, CancellationToken ct)
    {
        var ids = types.Select(t => t.Id).ToList();
        var counts = await db.Documents.AsNoTracking()
            .Where(d => ids.Contains(d.DocumentTypeId))
            .GroupBy(d => d.DocumentTypeId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var usage = await db.SubmissionRequirements.AsNoTracking()
            .Where(r => ids.Contains(r.DocumentTypeId)
                && (r.RequirementSet.Status == RequirementSetStatus.Published || r.RequirementSet.Status == RequirementSetStatus.Draft))
            .Select(r => new
            {
                r.DocumentTypeId,
                r.IsRequired,
                r.RequirementSet.ScopeType,
                r.RequirementSet.ScopeId,
                r.RequirementSet.SetVersion,
                r.RequirementSet.Status,
                SubmissionTypeName = r.RequirementSet.SubmissionType.Name,
            })
            .ToListAsync(ct);

        var replacementIds = types.Where(t => t.ReplacedByTypeId is not null).Select(t => t.ReplacedByTypeId!.Value).ToList();
        var replacementNames = await db.DocumentTypes.AsNoTracking()
            .Where(t => replacementIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var result = new List<DocumentTypeAdminDto>(types.Count);
        foreach (var t in types)
        {
            var usedBy = new List<DocumentTypeUsageDto>();
            foreach (var u in usage.Where(u => u.DocumentTypeId == t.Id))
            {
                var scope = await scopes.FindAsync(u.ScopeType, u.ScopeId, ct);
                if (scope is null) continue;
                usedBy.Add(new DocumentTypeUsageDto(u.ScopeType, u.ScopeId, scope.Code, scope.Name, u.SubmissionTypeName, u.SetVersion,
                    u.IsRequired, u.Status == RequirementSetStatus.Draft));
            }

            var documentCount = counts.GetValueOrDefault(t.Id);
            result.Add(new DocumentTypeAdminDto(
                t.Id, t.Code, t.Name, t.Description, t.NumberPattern, t.AcademicTag, t.Scope,
                t.RetentionPolicy is { } p ? new RetentionPolicyDto(p.Id, p.Name, p.RetainYears, p.Trigger, p.ActionAfter) : null,
                t.IsActive, t.ReplacedByTypeId,
                t.ReplacedByTypeId is { } r ? replacementNames.GetValueOrDefault(r) : null,
                documentCount, documentCount > 0,
                usedBy.OrderBy(u => u.Draft).ThenBy(u => RequirementPolicy.Rank(u.ScopeType)).ThenBy(u => u.ScopeCode).ToList(),
                t.UpdatedAt, ETags.From(t.Version)));
        }
        return result;
    }

    private async Task EnsureRetentionPolicyAsync(Guid? policyId, CancellationToken ct)
    {
        if (policyId is { } id && !await db.RetentionPolicies.AnyAsync(p => p.Id == id, ct))
            throw new ValidationAppException("retentionPolicyId", "Pick an existing retention policy.");
    }

    private static void ValidateCode(string code, FieldErrors errors) =>
        errors.Check(CodeFormat().IsMatch(code), "code", "Use 1–12 capital letters or digits, starting with a letter (e.g. SYL).");

    private static void ValidateName(string? name, FieldErrors errors) =>
        errors.Check(!string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 120, "name", "Enter a name of at most 120 characters.");

    private static void ValidatePattern(string? pattern, FieldErrors errors)
    {
        if (pattern is null) return;
        errors.Check(pattern.Length <= 60 && PatternFormat().IsMatch(pattern) && pattern.Contains("{seq:", StringComparison.Ordinal),
            "numberPattern", "Use capitals, digits, - _ / . and the tokens {yyyy}, {yy} and {seq:N}; include {seq:N} (e.g. SYL-{yyyy}-{seq:4}).");
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
