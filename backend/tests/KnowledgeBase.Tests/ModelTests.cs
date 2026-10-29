using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeBase.Tests;

/// <summary>Builds the EF model and its PostgreSQL DDL without a database, so mapping mistakes fail fast.</summary>
public class ModelTests
{
    internal static KbDbContext NewContext() => new(
        new DbContextOptionsBuilder<KbDbContext>()
            .UseNpgsql("Host=localhost;Database=kbms_model_only")
            .UseSnakeCaseNamingConvention()
            .Options,
        TimeProvider.System);

    [Fact]
    public void Create_script_generates()
    {
        using var db = NewContext();
        var sql = db.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE documents", sql);
        Assert.Contains("search_vector tsvector GENERATED ALWAYS AS", sql);
        Assert.Contains("ux_academic_terms_one_active", sql);
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS pg_trgm", sql);
    }

    [Fact]
    public void Enums_are_stored_as_text()
    {
        using var db = NewContext();
        var status = db.Model.FindEntityType(typeof(Core.Domain.Workflow.Submission))!.FindProperty("Status")!;

        Assert.Equal("character varying(40)", status.GetColumnType());
    }
}
