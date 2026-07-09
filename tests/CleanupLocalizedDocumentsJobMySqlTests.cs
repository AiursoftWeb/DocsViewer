using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.MySql;
using Aiursoft.DocsViewer.Services.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.DocsViewer.Tests;

/// <summary>
/// MySQL integration test for CleanupLocalizedDocumentsJob.
/// You need a local MySQL: docker run -d --name htc-mysql-test -e MYSQL_ROOT_PASSWORD=test123 -e MYSQL_DATABASE=HowToCookViewer -p 3307:3306 hub.aiursoft.com/mysql:9.7.0
/// </summary>
[TestClass]
public class CleanupLocalizedDocumentsJobMySqlTests
{
    private const string ConnectionString = "Server=localhost;Port=3307;Database=HowToCookViewer;Uid=root;Pwd=test123;";

    private DbContextOptions<MySqlContext> CreateOptions() =>
        new DbContextOptionsBuilder<MySqlContext>()
            .UseMySql(ConnectionString, new MySqlServerVersion(new Version(9, 7, 0)))
            .Options;

    [TestInitialize]
    public async Task Initialize()
    {
        try
        {
            await using var db = new MySqlContext(CreateOptions());
            await db.Database.EnsureCreatedAsync();
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"MySQL integration test skipped — infrastructure not available. ({ex.GetType().Name}: {ex.Message})");
        }
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await using var db = new MySqlContext(CreateOptions());
        try
        {
            await db.Database.EnsureDeletedAsync();
        }
        catch
        {
            // MySQL not available — nothing to clean up.
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_MySql_DeletesOrphanedAndStaleRows_WithoutError1093()
    {
        // Seed
        await using (var db = new MySqlContext(CreateOptions()))
        {
            db.GlobalSettings.Add(new GlobalSetting { Key = SettingsMap.LocalizationLanguages, Value = "en" });
            db.Documents.Add(new Document { Id = 1, Title = "Deleted", Category = "test", FilePath = "d/1.md", FileLastModified = DateTime.UtcNow, IsDeleted = true, Content = "Test" });
            db.Documents.Add(new Document { Id = 2, Title = "Active", Category = "test", FilePath = "d/2.md", FileLastModified = DateTime.UtcNow, Content = "Test" });
            db.LocalizedDocuments.Add(new LocalizedDocument { Id = 1, DocumentId = 1, Culture = "en", LocalizedTitle = "Orphaned", LocalizedContent = "Test", LastLocalizedAt = DateTime.UtcNow.AddHours(-2) });
            db.LocalizedDocuments.Add(new LocalizedDocument { Id = 3, DocumentId = 2, Culture = "en", LocalizedTitle = "Valid", LocalizedContent = "Test", LastLocalizedAt = DateTime.UtcNow.AddHours(-2) });
            await db.SaveChangesAsync();
        }

        // Act
        await using (var db = new MySqlContext(CreateOptions()))
        {
            var job = new CleanupLocalizedDocumentsJob(db, NullLogger<CleanupLocalizedDocumentsJob>.Instance);

            await job.ExecuteAsync();
        }

        // Assert
        await using (var db = new MySqlContext(CreateOptions()))
        {
            var remaining = await db.LocalizedDocuments.IgnoreQueryFilters().ToListAsync();
            Assert.AreEqual(1, remaining.Count, $"Expected 1 row, got {remaining.Count}: {string.Join(", ", remaining.Select(r => $"#{r.Id}"))}");
            Assert.AreEqual(3, remaining[0].Id, "Only the valid row (#3) should survive.");
            Assert.AreEqual(2, remaining[0].DocumentId);
            Assert.AreEqual("en", remaining[0].Culture);
        }
    }
}
