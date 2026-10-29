using KnowledgeBase.Core.Domain.Calendar;
using KnowledgeBase.Core.Domain.Org;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnowledgeBase.Core.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.HasIndex(x => x.ExternalId).IsUnique();
        b.Property(x => x.ExternalId).HasMaxLength(200);
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Title).HasMaxLength(120);
        b.Ignore(x => x.Initials);
    }
}

internal sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasIndex(x => x.ExternalId).IsUnique();
    }
}

internal sealed class GroupMemberConfiguration : IEntityTypeConfiguration<GroupMember>
{
    public void Configure(EntityTypeBuilder<GroupMember> b)
    {
        b.HasKey(x => new { x.GroupId, x.UserId });
        b.HasOne(x => x.Group).WithMany(g => g.Members).HasForeignKey(x => x.GroupId);
        b.HasOne(x => x.User).WithMany(u => u.Groups).HasForeignKey(x => x.UserId);
    }
}

internal sealed class CollegeConfiguration : IEntityTypeConfiguration<College>
{
    public void Configure(EntityTypeBuilder<College> b)
    {
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(200);
    }
}

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.ShortName).HasMaxLength(120);
        b.HasOne(x => x.College).WithMany(c => c.Departments).HasForeignKey(x => x.CollegeId).OnDelete(DeleteBehavior.Restrict);
        // Academic departments sit under a college; non-academic ones (CICT, Registrar…) don't.
        b.ToTable(t => t.HasCheckConstraint("ck_departments_kind_college",
            "(kind = 'Academic' AND college_id IS NOT NULL) OR (kind = 'NonAcademic' AND college_id IS NULL)"));
    }
}

internal sealed class AcademicProgramConfiguration : IEntityTypeConfiguration<AcademicProgram>
{
    public void Configure(EntityTypeBuilder<AcademicProgram> b)
    {
        b.ToTable("academic_programs");
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasOne(x => x.Department).WithMany(d => d.Programs).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FacultyProgramConfiguration : IEntityTypeConfiguration<FacultyProgram>
{
    public void Configure(EntityTypeBuilder<FacultyProgram> b)
    {
        b.HasKey(x => new { x.UserId, x.ProgramId });
        b.HasOne(x => x.User).WithMany(u => u.Programs).HasForeignKey(x => x.UserId);
        b.HasOne(x => x.Program).WithMany(p => p.Faculty).HasForeignKey(x => x.ProgramId);
        b.HasIndex(x => x.ProgramId);
    }
}

internal sealed class DepartmentStaffConfiguration : IEntityTypeConfiguration<DepartmentStaff>
{
    public void Configure(EntityTypeBuilder<DepartmentStaff> b)
    {
        b.HasKey(x => new { x.UserId, x.DepartmentId });
        b.HasOne(x => x.User).WithMany(u => u.StaffOf).HasForeignKey(x => x.UserId);
        b.HasOne(x => x.Department).WithMany(d => d.Staff).HasForeignKey(x => x.DepartmentId);
        b.Property(x => x.Position).HasMaxLength(120);
        b.HasIndex(x => x.DepartmentId);
    }
}

internal sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> b)
    {
        b.HasOne(x => x.User).WithMany(u => u.Roles).HasForeignKey(x => x.UserId);
        b.Property(x => x.Title).HasMaxLength(60);
        b.HasIndex(x => x.UserId);
        // One active Dean per college, one active AS per program, one active Head per department.
        b.HasIndex(x => new { x.Role, x.ScopeId }).IsUnique()
            .HasFilter("is_active AND role IN ('Dean', 'AcademicSupervisor', 'DepartmentHead')");
        b.ToTable(t => t.HasCheckConstraint("ck_role_assignments_scope",
            "(role IN ('SystemAdmin', 'Vpaf') AND scope_type = 'Global' AND scope_id IS NULL) OR " +
            "(role = 'Dean' AND scope_type = 'College' AND scope_id IS NOT NULL) OR " +
            "(role = 'AcademicSupervisor' AND scope_type = 'Program' AND scope_id IS NOT NULL) OR " +
            "(role = 'DepartmentHead' AND scope_type = 'Department' AND scope_id IS NOT NULL)"));
    }
}

internal sealed class CourseConfiguration : IEntityTypeConfiguration<Course>
{
    public void Configure(EntityTypeBuilder<Course> b)
    {
        b.HasOne(x => x.Program).WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Title).HasMaxLength(200);
        b.HasIndex(x => new { x.ProgramId, x.Code }).IsUnique();
    }
}

internal sealed class TeachingAssignmentConfiguration : IEntityTypeConfiguration<TeachingAssignment>
{
    public void Configure(EntityTypeBuilder<TeachingAssignment> b)
    {
        b.HasOne(x => x.AcademicTerm).WithMany().HasForeignKey(x => x.AcademicTermId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Section).HasMaxLength(40);
        b.HasIndex(x => new { x.AcademicTermId, x.UserId, x.CourseId, x.Section }).IsUnique();
        b.HasIndex(x => new { x.AcademicTermId, x.CourseId });
        b.HasIndex(x => x.UserId);
    }
}

internal sealed class TeachingLoadImportConfiguration : IEntityTypeConfiguration<TeachingLoadImport>
{
    public void Configure(EntityTypeBuilder<TeachingLoadImport> b)
    {
        b.HasOne<AcademicTerm>().WithMany().HasForeignKey(x => x.AcademicTermId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ImportedBy).WithMany().HasForeignKey(x => x.ImportedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.FileName).HasMaxLength(260);
        b.HasIndex(x => new { x.AcademicTermId, x.ImportedAt });
    }
}

internal sealed class AcademicYearConfiguration : IEntityTypeConfiguration<AcademicYear>
{
    public void Configure(EntityTypeBuilder<AcademicYear> b)
    {
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(60);
        b.ToTable(t => t.HasCheckConstraint("ck_academic_years_dates", "starts_on < ends_on"));
    }
}

internal sealed class AcademicTermConfiguration : IEntityTypeConfiguration<AcademicTerm>
{
    public void Configure(EntityTypeBuilder<AcademicTerm> b)
    {
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(60);
        b.Property(x => x.ShortName).HasMaxLength(30);
        b.HasOne(x => x.AcademicYear).WithMany(y => y.Terms).HasForeignKey(x => x.AcademicYearId).OnDelete(DeleteBehavior.Restrict);
        // Exactly one Active term at a time.
        b.HasIndex(x => x.Status).IsUnique().HasFilter("status = 'Active'").HasDatabaseName("ux_academic_terms_one_active");
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_academic_terms_dates", "starts_on < ends_on");
            t.HasCheckConstraint("ck_academic_terms_window",
                "(submission_opens_on IS NULL OR submission_opens_on >= starts_on) AND " +
                "(submission_closes_on IS NULL OR submission_closes_on <= ends_on) AND " +
                "(submission_opens_on IS NULL OR submission_closes_on IS NULL OR submission_opens_on <= submission_closes_on)");
        });
    }
}
