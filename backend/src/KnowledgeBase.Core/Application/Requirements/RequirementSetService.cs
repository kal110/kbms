using System.Text.RegularExpressions;
using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Application.Settings;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.DocumentTypes;
using KnowledgeBase.Core.Domain.Org;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Domain.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Requirements;

/// <summary>
/// Requirement sets per college, department and program (frames 18, 23, 24). A scope keeps one
/// Published set and at most one Draft per submission type; publishing a draft supersedes the
/// previous version, which stays in force for terms before the new version's effective term.
/// </summary>
public sealed partial class RequirementSetService(
    KbDbContext db,
    ICurrentUser currentUser,
    IAuditWriter audit,
    ScopeDirectory scopes,
    IRequirementResolver resolver,
    TimeProvider clock)
{
    private const int MaxItems = 50;
    private const int MaxFilesPerSlot = 20;

    private static readonly SubmissionStatus[] OpenStatuses =
    [
        SubmissionStatus.Draft, SubmissionStatus.Submitted, SubmissionStatus.UnderReview,
        SubmissionStatus.RevisionRequested, SubmissionStatus.Resubmitted,
    ];

    [GeneratedRegex("^[a-z][a-z0-9_]{0,59}$")]
    private static partial Regex KeyFormat();

    public async Task<ScopeRequirementSetsDto> ListForScopeAsync(string scopeKind, Guid scopeId, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var scope = await VisibleScopeAsync(me, ScopeDirectory.Parse(scopeKind), scopeId, ct);

        var sets = await db.RequirementSets.AsNoTracking()
            .Include(s => s.Requirements)
            .Include(s => s.SubmissionType).ThenInclude(t => t.Stages)
            .Where(s => s.ScopeType == scope.Type && s.ScopeId == scope.Id && s.Status != RequirementSetStatus.Superseded)
            .ToListAsync(ct);
        var lookups = await LookupsAsync(sets, ct);
        var inherited = await InheritedAsync(scope, lookups, ct);

        var types = await db.SubmissionTypes.AsNoTracking().Include(t => t.Stages).ToListAsync(ct);
        var shownTypeIds = sets.Select(s => s.SubmissionTypeId)
            .Concat(inherited.Keys.Where(id => types.Any(t => t.Id == id && t.IsActive && Allows(scope, t))))
            .Distinct()
            .ToList();

        var groups = shownTypeIds
            .Select(typeId =>
            {
                var type = types.Single(t => t.Id == typeId);
                var published = sets.FirstOrDefault(s => s.SubmissionTypeId == typeId && s.Status == RequirementSetStatus.Published);
                var draft = sets.FirstOrDefault(s => s.SubmissionTypeId == typeId && s.Status == RequirementSetStatus.Draft);
                return new ScopeSetGroupDto(TypeRef(type),
                    published is null ? null : ToDto(published, scope, lookups),
                    draft is null ? null : ToDto(draft, scope, lookups),
                    inherited.GetValueOrDefault(typeId) ?? []);
            })
            .OrderBy(g => g.SubmissionType.Name)
            .ToList();

        var available = types.Where(t => t.IsActive && Allows(scope, t)).OrderBy(t => t.Name).Select(TypeRef).ToList();
        return new ScopeRequirementSetsDto(scope.ToSource(), scope.Kind, RequirementPolicy.CanManage(me, scope), groups, available);
    }

    public async Task<RequirementSetDto> GetAsync(Guid setId, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var set = await LoadSetAsync(setId, tracking: false, ct);
        var scope = await VisibleScopeAsync(me, set.ScopeType, set.ScopeId, ct, notFound: ("Requirement set", setId));
        return ToDto(set, scope, await LookupsAsync([set], ct));
    }

    /// <summary>
    /// Starts a draft from the scope's published set (or another published set, for "copy from
    /// another program"). It takes effect next term when a published set exists, else this term.
    /// </summary>
    public async Task<RequirementSetDto> CreateDraftAsync(string scopeKind, Guid scopeId, CreateRequirementSetRequest request, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var scope = await ManagedScopeAsync(me, ScopeDirectory.Parse(scopeKind), scopeId, ct);

        var type = await db.SubmissionTypes.AsNoTracking().Include(t => t.Stages)
            .SingleOrDefaultAsync(t => t.Id == request.SubmissionTypeId && t.IsActive, ct)
            ?? throw new ValidationAppException("submissionTypeId", "Pick an active submission type.");
        if (!Allows(scope, type))
            throw new BusinessRuleException("type-not-allowed", $"“{type.Name}” requirements can't be set at this level.");

        var existing = await db.RequirementSets.AsNoTracking()
            .Where(s => s.ScopeType == scope.Type && s.ScopeId == scope.Id && s.SubmissionTypeId == type.Id)
            .Select(s => new { s.Id, s.Status, s.SetVersion })
            .ToListAsync(ct);
        if (existing.FirstOrDefault(s => s.Status == RequirementSetStatus.Draft) is { } openDraft)
            throw new ConflictException("draft-exists", "This list already has an unpublished draft.").With("draftId", openDraft.Id);

        var publishedId = existing.Where(s => s.Status == RequirementSetStatus.Published).Select(s => (Guid?)s.Id).FirstOrDefault();
        var sourceId = request.CopyFromSetId ?? publishedId;
        List<SubmissionRequirement> sourceItems = [];
        if (sourceId is { } from)
        {
            var source = await db.RequirementSets.AsNoTracking().Include(s => s.Requirements).SingleOrDefaultAsync(s => s.Id == from, ct);
            if (source is null || source.SubmissionTypeId != type.Id || source.Status == RequirementSetStatus.Draft)
                throw new BusinessRuleException("copy-source-invalid", "Copy from a published list of the same submission type.");
            sourceItems = source.Requirements.OrderBy(r => r.SortOrder).ToList();
        }

        var set = new RequirementSet
        {
            ScopeType = scope.Type,
            ScopeId = scope.Id,
            SubmissionTypeId = type.Id,
            SetVersion = existing.Select(s => s.SetVersion).DefaultIfEmpty(0).Max() + 1,
            Status = RequirementSetStatus.Draft,
            EffectiveFromTermId = await DefaultEffectiveTermAsync(publishedId is not null, ct),
            CreatedByUserId = me.UserId,
            Requirements = sourceItems.Select((r, i) => new SubmissionRequirement
            {
                Key = r.Key,
                Label = r.Label,
                DocumentTypeId = r.DocumentTypeId,
                IsRequired = r.IsRequired,
                MinFiles = r.MinFiles,
                MaxFiles = r.MaxFiles,
                SortOrder = i,
                DueOn = r.DueOn,
            }).ToList(),
        };
        db.RequirementSets.Add(set);
        audit.Record("requirement-set.draft-created", "requirement-set", set.Id, metadata: new
        {
            Scope = scope.Type, ScopeId = scope.Id, SubmissionType = type.Code, set.SetVersion, CopiedFrom = sourceId,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with another draft (ux_requirement_sets_one_draft).
            throw new ConflictException("draft-exists", "This list already has an unpublished draft.");
        }
        return await GetAsync(set.Id, ct);
    }

    public async Task<RequirementSetDto> UpdateDraftAsync(Guid setId, uint? expectedVersion, UpdateRequirementSetRequest request, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var set = await LoadSetAsync(setId, tracking: true, ct);
        var scope = await ManagedScopeAsync(me, set.ScopeType, set.ScopeId, ct, notFound: ("Requirement set", setId));
        RequireDraft(set);
        ETags.Check(expectedVersion, set);

        var errors = new FieldErrors();
        errors.Check((request.Notes?.Length ?? 0) <= 1000, "notes", "Use at most 1000 characters.");
        var items = ValidateItems(request.Items, errors);
        await ValidateDocumentTypesAsync(items, scope, errors, ct);
        if (request.EffectiveFromTermId is { } termId)
        {
            var status = await db.AcademicTerms.Where(t => t.Id == termId).Select(t => (TermStatus?)t.Status).SingleOrDefaultAsync(ct);
            errors.Check(status is not null, "effectiveFromTermId", "Pick an existing academic term.");
            errors.Check(status != TermStatus.Closed, "effectiveFromTermId", "Pick the active or a planned term.");
        }
        errors.ThrowIfAny();
        await RequireUniqueTypesAsync(scope, set.SubmissionTypeId,
            items.Select(i => (i.Key, i.Input.Label.Trim(), i.Input.DocumentTypeId)).ToList(), ct);

        set.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        set.EffectiveFromTermId = request.EffectiveFromTermId;
        // Edit slots in place by key so (set, key) stays unique at every step of the save.
        var byKey = set.Requirements.ToDictionary(r => r.Key, StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var (key, input, min, max) = items[i];
            if (!byKey.TryGetValue(key, out var slot))
            {
                slot = new SubmissionRequirement { Key = key, Label = input.Label.Trim() };
                set.Requirements.Add(slot);
            }
            slot.Label = input.Label.Trim();
            slot.DocumentTypeId = input.DocumentTypeId;
            slot.IsRequired = input.Rule == RequirementRule.Required;
            slot.MinFiles = min;
            slot.MaxFiles = max;
            slot.SortOrder = i;
            slot.DueOn = input.Due;
        }
        var keep = items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in set.Requirements.Where(r => !keep.Contains(r.Key)).ToList())
        {
            set.Requirements.Remove(removed);
            db.SubmissionRequirements.Remove(removed);
        }
        // Touch the set itself so its ETag changes even when only slots changed.
        set.UpdatedAt = clock.GetUtcNow();

        audit.Record("requirement-set.updated", "requirement-set", set.Id, metadata: new
        {
            set.SetVersion, Items = items.Count, set.EffectiveFromTermId,
        });
        await db.SaveChangesAsync(ct);
        return await GetAsync(set.Id, ct);
    }

    public async Task<RequirementSetDto> PublishAsync(Guid setId, uint? expectedVersion, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var set = await LoadSetAsync(setId, tracking: true, ct);
        var scope = await ManagedScopeAsync(me, set.ScopeType, set.ScopeId, ct, notFound: ("Requirement set", setId));
        RequireDraft(set);
        ETags.Check(expectedVersion, set);

        if (set.Requirements.Count == 0)
            throw new BusinessRuleException("set-empty", "Add at least one required document before publishing.");
        var typeIds = set.Requirements.Select(r => r.DocumentTypeId).Distinct().ToList();
        var retired = await db.DocumentTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id) && !t.IsActive).Select(t => t.Id).ToListAsync(ct);
        if (retired.Count > 0)
        {
            throw new BusinessRuleException("type-retired", "Some slots ask for a retired document type.")
                .With("keys", set.Requirements.Where(r => retired.Contains(r.DocumentTypeId)).Select(r => r.Key).ToList());
        }
        if (set.EffectiveFromTermId is { } termId
            && await db.AcademicTerms.AnyAsync(t => t.Id == termId && t.Status == TermStatus.Closed, ct))
        {
            throw new BusinessRuleException("term-closed", "The effective term is closed; pick the active or a planned term.");
        }
        // Again at publish: a college or department list may have added one of these types since the draft was saved.
        await RequireUniqueTypesAsync(scope, set.SubmissionTypeId,
            set.Requirements.OrderBy(r => r.SortOrder).Select(r => (r.Key, r.Label, r.DocumentTypeId)).ToList(), ct);

        var previous = await db.RequirementSets
            .Where(s => s.ScopeType == set.ScopeType && s.ScopeId == set.ScopeId && s.SubmissionTypeId == set.SubmissionTypeId
                && s.Status == RequirementSetStatus.Published && s.Id != set.Id)
            .ToListAsync(ct);
        foreach (var p in previous) p.Status = RequirementSetStatus.Superseded;

        set.Status = RequirementSetStatus.Published;
        set.PublishedByUserId = me.UserId;
        set.PublishedAt = clock.GetUtcNow();
        audit.Record("requirement-set.published", "requirement-set", set.Id, metadata: new
        {
            Scope = scope.Type, ScopeId = scope.Id, set.SetVersion, Superseded = previous.Select(p => p.Id).ToList(),
        });
        await db.SaveChangesAsync(ct);
        return await GetAsync(set.Id, ct);
    }

    public async Task DeleteDraftAsync(Guid setId, uint? expectedVersion, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var set = await LoadSetAsync(setId, tracking: true, ct);
        await ManagedScopeAsync(me, set.ScopeType, set.ScopeId, ct, notFound: ("Requirement set", setId));
        RequireDraft(set);
        ETags.Check(expectedVersion, set);

        db.RequirementSets.Remove(set);
        audit.Record("requirement-set.draft-deleted", "requirement-set", set.Id, metadata: new { set.SetVersion });
        await db.SaveChangesAsync(ct);
    }

    public async Task<RequirementScopeTreeDto> GetScopeTreeAsync(CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var all = await scopes.ListAsync(ct);
        var counts = await db.RequirementSets.AsNoTracking()
            .Where(s => s.Status != RequirementSetStatus.Superseded)
            .GroupBy(s => new { s.ScopeType, s.ScopeId, s.Status })
            .Select(g => new { g.Key.ScopeType, g.Key.ScopeId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        RequirementScopeNodeDto Node(ScopeInfo s, IReadOnlyList<RequirementScopeNodeDto> children)
        {
            int Count(RequirementSetStatus status) =>
                counts.Where(c => c.ScopeType == s.Type && c.ScopeId == s.Id && c.Status == status).Sum(c => c.Count);
            return new RequirementScopeNodeDto(s.Type, s.Id, s.Code, s.Name, s.Kind, RequirementPolicy.CanManage(me, s),
                Count(RequirementSetStatus.Published), Count(RequirementSetStatus.Draft), children);
        }

        List<RequirementScopeNodeDto> academic;
        if (me.IsAdmin || me.IsVpaf)
        {
            academic = all.Where(s => s.Type == RequirementScope.College).OrderBy(s => s.Code)
                .Select(c => Node(c, all
                    .Where(d => d.Type == RequirementScope.Department && d.Kind == DepartmentKind.Academic && d.CollegeId == c.Id)
                    .OrderBy(d => d.Name)
                    .Select(d => Node(d, all
                        .Where(p => p.Type == RequirementScope.Program && p.DepartmentId == d.Id)
                        .OrderBy(p => p.Code)
                        .Select(p => Node(p, []))
                        .ToList()))
                    .ToList()))
                .ToList();
        }
        else
        {
            // An Academic Supervisor gets their programs to read (CanManage is false).
            academic = all.Where(s => s.Type == RequirementScope.Program && me.IsSupervisorOf(s.Id))
                .OrderBy(s => s.Code).Select(p => Node(p, [])).ToList();
        }

        var nonAcademic = all
            .Where(s => s.Type == RequirementScope.Department && s.Kind == DepartmentKind.NonAcademic && RequirementPolicy.CanManage(me, s))
            .OrderBy(s => s.Name)
            .Select(d => Node(d, []))
            .ToList();
        return new RequirementScopeTreeDto(academic, nonAcademic);
    }

    /// <summary>
    /// The caller's checklist (or, for admins, another user's) for a submission type and term; with a
    /// program (a course's program), the checklist of that program instead of the primary one; with a
    /// department (faculty who are also staff), that department's.
    /// </summary>
    public async Task<EffectiveChecklistDto> GetEffectiveAsync(
        Guid submissionTypeId, Guid? termId, Guid? userId, Guid? programId = null, Guid? departmentId = null, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        var target = userId ?? me.UserId;
        if (target != me.UserId && !me.IsAdmin) throw new ForbiddenException("Only an administrator can look up someone else's checklist.");

        var type = await db.SubmissionTypes.AsNoTracking().Include(t => t.Stages).SingleOrDefaultAsync(t => t.Id == submissionTypeId, ct)
            ?? throw new NotFoundException("Submission type", submissionTypeId);
        var term = termId is { } id
            ? await db.AcademicTerms.AsNoTracking().Include(t => t.AcademicYear).SingleOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new NotFoundException("Academic term", id)
            : await db.AcademicTerms.AsNoTracking().Include(t => t.AcademicYear).SingleOrDefaultAsync(t => t.Status == TermStatus.Active, ct)
                ?? throw new NotFoundException("Active academic term");

        var checklist = await resolver.GetEffectiveChecklistAsync(target, type.Id, term.Id, programId, departmentId, ct);
        var types = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var items = new List<RequirementItemDto>(checklist.Requirements.Count);
        foreach (var r in checklist.Requirements)
        {
            var source = await scopes.FindAsync(r.Scope, r.ScopeId, ct);
            items.Add(Item(r.RequirementId, r.Key, r.Label, r.DocumentTypeId, r.IsRequired, r.MinFiles, r.MaxFiles, r.DueOn, types, source?.ToSource()));
        }
        return new EffectiveChecklistDto(TypeRef(type), TermRef(term), checklist.RequirementSetIds, items);
    }

    /// <summary>Published lists of the same submission type elsewhere, for "Copy from another program".</summary>
    public async Task<IReadOnlyList<RequirementSetDto>> GetCopySourcesAsync(Guid submissionTypeId, CancellationToken ct = default)
    {
        var me = await currentUser.GetAsync(ct);
        if (!me.IsAdmin && !me.IsVpaf)
            throw new ForbiddenException("Only people who manage a requirement list can copy one.");

        var sets = await db.RequirementSets.AsNoTracking()
            .Include(s => s.Requirements)
            .Include(s => s.SubmissionType).ThenInclude(t => t.Stages)
            .Where(s => s.SubmissionTypeId == submissionTypeId && s.Status == RequirementSetStatus.Published)
            .ToListAsync(ct);
        var lookups = await LookupsAsync(sets, ct);
        var result = new List<RequirementSetDto>();
        foreach (var set in sets)
        {
            if (await scopes.FindAsync(set.ScopeType, set.ScopeId, ct) is { } scope) result.Add(ToDto(set, scope, lookups));
        }
        return result.OrderBy(s => RequirementPolicy.Rank(s.Scope.Type)).ThenBy(s => s.Scope.Code).ToList();
    }

    // ---- helpers ----

    private sealed record Lookups(
        IReadOnlyDictionary<Guid, DocumentType> Types,
        IReadOnlyDictionary<Guid, string> People,
        IReadOnlyDictionary<Guid, TermRefDto> Terms,
        IReadOnlyDictionary<Guid, int> OpenSubmissions);

    private async Task<Lookups> LookupsAsync(IReadOnlyCollection<RequirementSet> sets, CancellationToken ct)
    {
        var types = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var personIds = sets.Select(s => s.CreatedByUserId)
            .Concat(sets.Where(s => s.PublishedByUserId is not null).Select(s => s.PublishedByUserId!.Value))
            .Distinct().ToList();
        var people = await db.Users.AsNoTracking().Where(u => personIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var terms = (await db.AcademicTerms.AsNoTracking().Include(t => t.AcademicYear).ToListAsync(ct)).ToDictionary(t => t.Id, TermRef);

        var setIds = sets.Select(s => s.Id).ToList();
        var open = await db.SubmissionRequirementSets.AsNoTracking()
            .Where(x => setIds.Contains(x.RequirementSetId))
            .Join(db.Submissions.Where(s => OpenStatuses.Contains(s.Status)), x => x.SubmissionId, s => s.Id, (x, s) => x.RequirementSetId)
            .GroupBy(id => id)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return new Lookups(types, people, terms, open);
    }

    /// <summary>Per submission type, the merged published slots of the broader scopes.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<RequirementItemDto>>> InheritedAsync(ScopeInfo scope, Lookups lookups, CancellationToken ct)
    {
        var broader = new List<(RequirementScope Type, Guid Id)>();
        if (scope.Type == RequirementScope.Program && scope.DepartmentId is { } dept) broader.Add((RequirementScope.Department, dept));
        if (scope.Type != RequirementScope.College && scope.CollegeId is { } college) broader.Add((RequirementScope.College, college));
        if (broader.Count == 0) return [];

        var ids = broader.Select(b => b.Id).ToList();
        var sets = await db.RequirementSets.AsNoTracking().Include(s => s.Requirements)
            .Where(s => ids.Contains(s.ScopeId) && s.Status == RequirementSetStatus.Published)
            .ToListAsync(ct);
        sets = sets.Where(s => broader.Contains((s.ScopeType, s.ScopeId))).ToList();

        var result = new Dictionary<Guid, IReadOnlyList<RequirementItemDto>>();
        foreach (var byType in sets.GroupBy(s => s.SubmissionTypeId))
        {
            var merged = Checklists.Merge(byType.Select(s => new ChecklistLayer(s.ScopeType, s.ScopeId, s.Id, s.Requirements)));
            var items = new List<RequirementItemDto>(merged.Count);
            foreach (var r in merged)
            {
                var source = await scopes.FindAsync(r.Scope, r.ScopeId, ct);
                items.Add(Item(r.RequirementId, r.Key, r.Label, r.DocumentTypeId, r.IsRequired, r.MinFiles, r.MaxFiles, r.DueOn,
                    lookups.Types, source?.ToSource()));
            }
            result[byType.Key] = items;
        }
        return result;
    }

    private RequirementSetDto ToDto(RequirementSet set, ScopeInfo scope, Lookups lookups) => new(
        set.Id,
        scope.ToSource(),
        TypeRef(set.SubmissionType),
        set.SetVersion,
        set.Status,
        set.EffectiveFromTermId is { } termId ? lookups.Terms.GetValueOrDefault(termId) : null,
        set.Notes,
        lookups.People.TryGetValue(set.CreatedByUserId, out var creator) ? new PersonRefDto(set.CreatedByUserId, creator) : null,
        set.PublishedByUserId is { } p && lookups.People.TryGetValue(p, out var publisher) ? new PersonRefDto(p, publisher) : null,
        set.PublishedAt,
        set.UpdatedAt,
        lookups.OpenSubmissions.GetValueOrDefault(set.Id),
        set.Requirements.OrderBy(r => r.SortOrder)
            .Select(r => Item(r.Id, r.Key, r.Label, r.DocumentTypeId, r.IsRequired, r.MinFiles, r.MaxFiles, r.DueOn, lookups.Types, null))
            .ToList(),
        ETags.From(set.Version));

    private static RequirementItemDto Item(Guid id, string key, string label, Guid documentTypeId, bool isRequired, int min, int max,
        DateOnly? due, IReadOnlyDictionary<Guid, DocumentType> types, ScopeSourceDto? source)
    {
        var type = types.GetValueOrDefault(documentTypeId);
        return new RequirementItemDto(id, key, label, documentTypeId, type?.Code ?? "", type?.Name ?? "",
            isRequired ? RequirementRule.Required : RequirementRule.Optional, new FileLimit(min, max), due, source);
    }

    private static SubmissionTypeRefDto TypeRef(SubmissionType t) =>
        new(t.Id, t.Code, t.Name, t.ProgramScoped, t.Stages.Any(s => s.ReviewerRule == ReviewerRule.CollegeDean));

    private static TermRefDto TermRef(AcademicTerm t) => new(t.Id, t.Code, t.Name, t.ShortName, t.AcademicYear.Name);

    /// <summary>Program lists are for program-scoped types; non-academic departments keep the office types.</summary>
    private static bool Allows(ScopeInfo scope, SubmissionType type) => scope.Type switch
    {
        RequirementScope.Program => type.ProgramScoped,
        RequirementScope.Department when scope.Kind == DepartmentKind.NonAcademic => !type.ProgramScoped,
        _ => true,
    };

    private static bool Compatible(TypeScope typeScope, ScopeInfo scope) =>
        typeScope == TypeScope.All
        || (scope.Kind == DepartmentKind.NonAcademic ? typeScope == TypeScope.NonAcademic : typeScope == TypeScope.Academic);

    private async Task<RequirementSet> LoadSetAsync(Guid setId, bool tracking, CancellationToken ct)
    {
        var query = db.RequirementSets.Include(s => s.Requirements).Include(s => s.SubmissionType).ThenInclude(t => t.Stages).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(s => s.Id == setId, ct) ?? throw new NotFoundException("Requirement set", setId);
    }

    private async Task<ScopeInfo> VisibleScopeAsync(UserContext me, RequirementScope type, Guid id, CancellationToken ct,
        (string Resource, Guid Id)? notFound = null)
    {
        var scope = await scopes.FindAsync(type, id, ct);
        if (scope is null || !RequirementPolicy.CanView(me, scope))
            throw notFound is { } nf ? new NotFoundException(nf.Resource, nf.Id) : new NotFoundException(type.ToString(), id);
        return scope;
    }

    private async Task<ScopeInfo> ManagedScopeAsync(UserContext me, RequirementScope type, Guid id, CancellationToken ct,
        (string Resource, Guid Id)? notFound = null)
    {
        var scope = await VisibleScopeAsync(me, type, id, ct, notFound);
        if (!RequirementPolicy.CanManage(me, scope))
            throw new ForbiddenException(scope.Type == RequirementScope.Program
                ? "Only an administrator or the VPAF can change a program's required documents. Its Academic Supervisor verifies submissions against them."
                : "Only an administrator or the VPAF can change college and department required documents.");
        return scope;
    }

    private static void RequireDraft(RequirementSet set)
    {
        if (set.Status != RequirementSetStatus.Draft)
            throw new ConflictException("set-not-draft", "Only a draft can change; start a new draft from the published list.");
    }

    /// <summary>The active term when the scope has nothing published yet, else the next planned term.</summary>
    private async Task<Guid?> DefaultEffectiveTermAsync(bool hasPublished, CancellationToken ct)
    {
        var active = await db.AcademicTerms.AsNoTracking().Where(t => t.Status == TermStatus.Active)
            .Select(t => new { t.Id, t.StartsOn }).SingleOrDefaultAsync(ct);
        if (!hasPublished && active is not null) return active.Id;

        var after = active?.StartsOn ?? DateOnly.MinValue;
        var next = await db.AcademicTerms.AsNoTracking()
            .Where(t => t.Status == TermStatus.Planned && t.StartsOn > after)
            .OrderBy(t => t.StartsOn)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
        return next ?? active?.Id;
    }

    private static List<(string Key, RequirementItemInput Input, int Min, int Max)> ValidateItems(
        IReadOnlyList<RequirementItemInput>? items, FieldErrors errors)
    {
        var result = new List<(string, RequirementItemInput, int, int)>();
        if (items is null)
        {
            errors.Add("items", "Send the full list of required documents.");
            return result;
        }
        errors.Check(items.Count <= MaxItems, "items", $"Use at most {MaxItems} slots.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var path = $"items[{i}]";
            if (item is null)
            {
                errors.Add(path, "Empty slot.");
                continue;
            }

            var labelOk = !string.IsNullOrWhiteSpace(item.Label) && item.Label.Trim().Length <= 200;
            errors.Check(labelOk, $"{path}.label", "Enter a label of at most 200 characters.");
            var key = string.IsNullOrWhiteSpace(item.Key) ? (labelOk ? Checklists.KeyFromLabel(item.Label) : "") : item.Key.Trim();
            if (!KeyFormat().IsMatch(key))
                errors.Add($"{path}.key", "Use lowercase letters, digits and underscores, starting with a letter.");
            else if (!seen.Add(key))
                errors.Add($"{path}.key", "Each slot needs its own key.");

            errors.Check(Enum.IsDefined(item.Rule), $"{path}.rule", "Use Required or Optional.");
            if (item.Files is null)
            {
                errors.Add($"{path}.files", "Set how many files the slot takes.");
                continue;
            }
            var (min, max) = Checklists.NormalizeFiles(item.Rule, item.Files);
            errors.Check(item.Rule != RequirementRule.Required || min >= 1, $"{path}.files", "A required slot needs at least one file.");
            errors.Check(min >= 0 && max >= Math.Max(1, min) && max <= MaxFilesPerSlot, $"{path}.files",
                $"Allow between {Math.Max(1, min)} and {MaxFilesPerSlot} files.");
            result.Add((key, item, min, max));
        }
        return result;
    }

    /// <summary>
    /// A document type appears once in a submitter's checklist (frame 24): not twice in this list, and
    /// not one that a broader published list (the college, or the program's department) already asks
    /// for. 422 duplicate-type with the field errors per slot ("items[2].documentTypeId") and their keys.
    /// </summary>
    private async Task RequireUniqueTypesAsync(ScopeInfo scope, Guid submissionTypeId,
        IReadOnlyList<(string Key, string Label, Guid DocumentTypeId)> items, CancellationToken ct)
    {
        var broader = new List<(RequirementScope Type, Guid Id)>();
        if (scope.Type == RequirementScope.Program && scope.DepartmentId is { } dept) broader.Add((RequirementScope.Department, dept));
        if (scope.Type != RequirementScope.College && scope.CollegeId is { } college) broader.Add((RequirementScope.College, college));

        // Type → the code of the broader scope that already asks for it.
        var inherited = new Dictionary<Guid, string>();
        if (broader.Count > 0)
        {
            var ids = broader.Select(b => b.Id).ToList();
            var sets = await db.RequirementSets.AsNoTracking()
                .Where(s => ids.Contains(s.ScopeId) && s.SubmissionTypeId == submissionTypeId && s.Status == RequirementSetStatus.Published)
                .Select(s => new { s.ScopeType, s.ScopeId, Types = s.Requirements.Select(r => r.DocumentTypeId).ToList() })
                .ToListAsync(ct);
            foreach (var s in sets.Where(s => broader.Contains((s.ScopeType, s.ScopeId))).OrderBy(s => RequirementPolicy.Rank(s.ScopeType)))
            {
                var code = (await scopes.FindAsync(s.ScopeType, s.ScopeId, ct))?.Code ?? s.ScopeType.ToString();
                foreach (var type in s.Types) inherited.TryAdd(type, code);
            }
        }

        var typeIds = items.Select(i => i.DocumentTypeId).Distinct().ToList();
        var codes = await db.DocumentTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Code, ct);
        var firstSlot = new Dictionary<Guid, string>();
        var errors = new Dictionary<string, string[]>();
        var keys = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            var (key, label, typeId) = items[i];
            var code = codes.GetValueOrDefault(typeId, "This type");
            string? message = inherited.TryGetValue(typeId, out var by) ? $"{code} is already required by the {by} list."
                : !firstSlot.TryAdd(typeId, label) ? $"{code} is already in this list (“{firstSlot[typeId]}”)."
                : null;
            if (message is null) continue;
            errors[$"items[{i}].documentTypeId"] = [message];
            keys.Add(key);
        }
        if (errors.Count > 0)
        {
            throw new BusinessRuleException("duplicate-type", "Each document type can appear only once in a checklist.")
                .With("errors", errors)
                .With("keys", keys);
        }
    }

    private async Task ValidateDocumentTypesAsync(
        IReadOnlyList<(string Key, RequirementItemInput Input, int Min, int Max)> items, ScopeInfo scope, FieldErrors errors, CancellationToken ct)
    {
        var ids = items.Select(i => i.Input.DocumentTypeId).Distinct().ToList();
        var types = await db.DocumentTypes.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        for (var i = 0; i < items.Count; i++)
        {
            var path = $"items[{i}].documentTypeId";
            if (!types.TryGetValue(items[i].Input.DocumentTypeId, out var type)) errors.Add(path, "Pick an existing document type.");
            else if (!type.IsActive) errors.Add(path, $"{type.Code} is retired; pick its replacement.");
            else if (!Compatible(type.Scope, scope))
                errors.Add(path, $"{type.Code} is a {(type.Scope == TypeScope.Academic ? "academic" : "non-academic")} type.");
        }
    }
}
