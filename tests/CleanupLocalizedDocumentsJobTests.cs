using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Services.BackgroundJobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public class CleanupLocalizedDocumentsJobTests
{
    // Concrete context for SQLite in-memory tests — enables ExecuteDeleteAsync
    // which the InMemory provider does not support.
    private sealed class SqliteTestContext(DbContextOptions<SqliteTestContext> options)
        : DocsViewerDbContext(options)
    {
    }

    private SqliteConnection _connection = null!;
    private DbContextOptions<SqliteTestContext> _dbOptions = null!;

    [TestInitialize]
    public void Initialize()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<SqliteTestContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new SqliteTestContext(_dbOptions);
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private async Task<CleanupLocalizedDocumentsJob> CreateJobAsync()
    {
        var db = new SqliteTestContext(_dbOptions);

        return new CleanupLocalizedDocumentsJob(
            db,
            NullLogger<CleanupLocalizedDocumentsJob>.Instance);
    }

    private static async Task SeedAsync(DocsViewerDbContext db, params object[] entities)
    {
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private static Document CreateDocument(int id, string title = "Test Document", bool deleted = false)
    {
        return new Document
        {
            Id = id,
            Title = title,
            Category = "test",
            FilePath = $"docs/test/{id}.md",
            FileLastModified = DateTime.UtcNow,
            IsDeleted = deleted,
            Content = "Test"
        };
    }

    private static LocalizedDocument CreateLocalized(int id, int documentId, string culture, DateTime? lastLocalized = null)
    {
        return new LocalizedDocument
        {
            Id = id,
            DocumentId = documentId,
            Culture = culture,
            LocalizedTitle = $"Document {documentId} in {culture}",
            LocalizedContent = "Test",
            LastLocalizedAt = lastLocalized ?? DateTime.UtcNow
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // 1. Deletes orphaned rows (parent Document soft-deleted)
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_DeletesOrphanedLocalizedDocuments()
    {
        var job = await CreateJobAsync();
        await using var db = new SqliteTestContext(_dbOptions);

        var deletedDocument = CreateDocument(1, "Deleted Document", deleted: true);
        var activeDocument = CreateDocument(2, "Active Document", deleted: false);
        var orphaned = CreateLocalized(1, 1, "en", DateTime.UtcNow.AddHours(-1));
        var validRow = CreateLocalized(2, 2, "en", DateTime.UtcNow.AddHours(-1));

        await SeedAsync(db, deletedDocument, activeDocument, orphaned, validRow);

        // Act
        await job.ExecuteAsync();

        // Assert
        var remaining = await db.LocalizedDocuments.IgnoreQueryFilters().ToListAsync();
        Assert.AreEqual(1, remaining.Count, "Only the valid row should remain.");
        Assert.AreEqual(2, remaining[0].DocumentId, "Active document's localized row should survive.");
    }


}
