using KnowledgeBase.Core.Application.Abstractions;
using KnowledgeBase.Core.Application.Security;
using KnowledgeBase.Core.Common;
using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.Library;
using KnowledgeBase.Core.Domain.Requirements;
using KnowledgeBase.Core.Domain.Workflow;
using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Core.Application.Settings;

/// <summary>
/// Academic years and terms (frames 19–20). Names and dates come from the SIS; admins set the
/// submission window and which term is active. Exactly one term is Active at a time.
/// </summary>
public sealed class AcademicCalendarService(KbDbContext db, ICurrentUser currentUser, IAuditWriter audit, TimeProvider clock)
{
    private static readonly SubmissionStatus[] OpenStatuses =
    [
        SubmissionStatus.Draft, SubmissionStatus.Submitted, SubmissionStatus.UnderReview,
        SubmissionStatus.RevisionRequested, SubmissionStatus.Resubmitted,
    ];

    public async Task<IReadOnlyList<AcademicYearDto>> ListYearsAsync(CancellationToken ct = default)
    {
        await currentUser.GetAsync(ct);
        return await LoadYearsAsync(withUsage: false, ct);
    }

    /// <summary>Settings → Academic calendar: every year and term with usage counts.</summary>
    public async Task<IReadOnlyList<AcademicYearDto>> ListYearsForAdminAsync(CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        return await LoadYearsAsync(withUsage: true, ct);
    }

    public async Task<AcademicTermDto> GetActiveTermAsync(CancellationToken ct = default)
    {
        await currentUser.GetAsync(ct);
        var term = await db.AcademicTerms.AsNoTracking().Include(t => t.AcademicYear)
            .SingleOrDefaultAsync(t => t.Status == TermStatus.Active, ct)
            ?? throw new NotFoundException("Active academic term");
        return ToDto(term, null);
    }

    public async Task<AcademicTermDto> UpdateTermAsync(Guid id, uint? expectedVersion, UpdateAcademicTermRequest request, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var term = await db.AcademicTerms.Include(t => t.AcademicYear).SingleOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Academic term", id);
        ETags.Check(expectedVersion, term);

        var windowChange = request.ClearSubmissionWindow == true || request.SubmissionOpensOn is not null || request.SubmissionClosesOn is not null;
        if (windowChange && term.Status == TermStatus.Closed)
            throw new BusinessRuleException("term-closed", "The submission window of a closed term can't change.");

        var opens = request.ClearSubmissionWindow == true ? null : request.SubmissionOpensOn ?? term.SubmissionOpensOn;
        var closes = request.ClearSubmissionWindow == true ? null : request.SubmissionClosesOn ?? term.SubmissionClosesOn;
        var errors = new FieldErrors();
        errors.Check(opens is null || (opens >= term.StartsOn && opens <= term.EndsOn), "submissionOpensOn",
            $"The window must open inside the term ({term.StartsOn:yyyy-MM-dd} – {term.EndsOn:yyyy-MM-dd}).");
        errors.Check(closes is null || (closes >= term.StartsOn && closes <= term.EndsOn), "submissionClosesOn",
            $"The window must close inside the term ({term.StartsOn:yyyy-MM-dd} – {term.EndsOn:yyyy-MM-dd}).");
        errors.Check(opens is null || closes is null || opens <= closes, "submissionClosesOn", "The window can't close before it opens.");
        errors.ThrowIfAny();

        term.SubmissionOpensOn = opens;
        term.SubmissionClosesOn = closes;
        if (request.AllowResubmitAfterClose is { } allow) term.AllowResubmitAfterClose = allow;

        audit.Record("academic-term.updated", "academic-term", term.Id, metadata: new
        {
            term.Code, term.SubmissionOpensOn, term.SubmissionClosesOn, term.AllowResubmitAfterClose,
        });
        await db.SaveChangesAsync(ct);
        return ToDto(term, null);
    }

    /// <summary>Makes a planned term the active one and closes the term that was active.</summary>
    public async Task<AcademicTermDto> ActivateTermAsync(Guid id, uint? expectedVersion, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var term = await db.AcademicTerms.Include(t => t.AcademicYear).SingleOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Academic term", id);
        ETags.Check(expectedVersion, term);
        if (term.Status == TermStatus.Active) return ToDto(term, null);
        if (term.Status == TermStatus.Closed)
            throw new BusinessRuleException("term-closed", "A closed term can't become active again.");

        var previous = await db.AcademicTerms.Where(t => t.Status == TermStatus.Active).ToListAsync(ct);
        if (previous.Any(p => p.StartsOn > term.StartsOn))
            throw new BusinessRuleException("term-out-of-order", "The active term starts after this one; activate terms in calendar order.");

        // Two saves in one transaction: the old term closes before the new one turns Active, so the
        // one-active-term unique index never sees two.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var p in previous) p.Status = TermStatus.Closed;
        if (previous.Count > 0) await db.SaveChangesAsync(ct);

        term.Status = TermStatus.Active;
        audit.Record("academic-term.activated", "academic-term", term.Id, metadata: new
        {
            term.Code, Closed = previous.Select(p => p.Code).ToList(),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(term, null);
    }

    public async Task<AcademicTermDto> CloseTermAsync(Guid id, uint? expectedVersion, CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        var term = await db.AcademicTerms.Include(t => t.AcademicYear).SingleOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Academic term", id);
        ETags.Check(expectedVersion, term);
        if (term.Status != TermStatus.Active)
            throw new BusinessRuleException("term-not-active", "Only the active term can be closed.");

        term.Status = TermStatus.Closed;
        audit.Record("academic-term.closed", "academic-term", term.Id, metadata: new { term.Code });
        await db.SaveChangesAsync(ct);
        return ToDto(term, null);
    }

    public async Task SyncFromSisAsync(CancellationToken ct = default)
    {
        (await currentUser.GetAsync(ct)).RequireAdmin();
        throw new FeatureDisabledException("SIS sync", "Years and terms are seeded until the SIS connector is configured.");
    }

    private async Task<IReadOnlyList<AcademicYearDto>> LoadYearsAsync(bool withUsage, CancellationToken ct)
    {
        var years = await db.AcademicYears.AsNoTracking().Include(y => y.Terms)
            .OrderByDescending(y => y.StartsOn)
            .ToListAsync(ct);
        var usage = withUsage ? await LoadUsageAsync(years.SelectMany(y => y.Terms).ToList(), ct) : null;

        return years.Select(y => new AcademicYearDto(
                y.Id, y.Code, y.Name, y.StartsOn, y.EndsOn, y.FromSis,
                y.Terms.OrderBy(t => t.SortOrder).ThenBy(t => t.StartsOn)
                    .Select(t =>
                    {
                        t.AcademicYear = y;
                        return ToDto(t, usage?.GetValueOrDefault(t.Id));
                    })
                    .ToList()))
            .ToList();
    }

    private async Task<Dictionary<Guid, AcademicTermUsageDto>> LoadUsageAsync(IReadOnlyList<AcademicTerm> terms, CancellationToken ct)
    {
        var documents = await db.Documents.AsNoTracking()
            .Where(d => d.AcademicTermId != null && d.LifecycleStatus != LifecycleStatus.Deleted)
            .GroupBy(d => d.AcademicTermId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var submissions = await db.Submissions.AsNoTracking()
            .GroupBy(s => new { s.AcademicTermId, s.Status })
            .Select(g => new { g.Key.AcademicTermId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        // A set is "in force" for a term from its effective term on, until a newer version takes over.
        var sets = await db.RequirementSets.AsNoTracking()
            .Where(s => s.Status != RequirementSetStatus.Draft)
            .Select(s => new { s.ScopeType, s.ScopeId, s.SubmissionTypeId, s.EffectiveFromTermId })
            .ToListAsync(ct);
        var starts = terms.ToDictionary(t => t.Id, t => t.StartsOn);

        return terms.ToDictionary(t => t.Id, t =>
        {
            var forTerm = submissions.Where(s => s.AcademicTermId == t.Id).ToList();
            var inForce = sets
                .Where(s => s.EffectiveFromTermId is null || (starts.TryGetValue(s.EffectiveFromTermId.Value, out var from) && from <= t.StartsOn))
                .Select(s => (s.ScopeType, s.ScopeId, s.SubmissionTypeId))
                .Distinct()
                .Count();
            return new AcademicTermUsageDto(
                documents.GetValueOrDefault(t.Id),
                forTerm.Sum(s => s.Count),
                forTerm.Where(s => OpenStatuses.Contains(s.Status)).Sum(s => s.Count),
                forTerm.Where(s => s.Status == SubmissionStatus.Approved).Sum(s => s.Count),
                inForce);
        });
    }

    private AcademicTermDto ToDto(AcademicTerm t, AcademicTermUsageDto? usage)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var open = t.Status == TermStatus.Active
            && today >= (t.SubmissionOpensOn ?? t.StartsOn)
            && today <= (t.SubmissionClosesOn ?? t.EndsOn);
        return new AcademicTermDto(
            t.Id, t.AcademicYearId, t.AcademicYear.Code, t.AcademicYear.Name, t.Code, t.Name, t.ShortName, t.SortOrder,
            t.StartsOn, t.EndsOn, t.Status, t.SubmissionOpensOn, t.SubmissionClosesOn, t.AllowResubmitAfterClose,
            open, t.FromSis, usage, ETags.From(t.Version));
    }
}
