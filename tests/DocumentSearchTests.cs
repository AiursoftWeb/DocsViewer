using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.InMemory;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public sealed class DocumentSearchTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MultiwordSearchPreservesRankingLocalizationFiltersAndPagination(bool relational)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using DocsViewerDbContext db = relational
            ? new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite(connection).Options)
            : new InMemoryContext(new DbContextOptionsBuilder<InMemoryContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await db.Database.EnsureCreatedAsync();

        var titleMatch = Document("Quartz deployment guide", "Setup instructions", "title.md");
        var contentMatch = Document("Background notes", "Quartz deployment requires a release switch.", "content.md");
        var localizedMatch = Document("Translated guide", "Source text", "localized.md");
        localizedMatch.LocalizedDocuments.Add(new LocalizedDocument
        {
            Culture = "fr-FR", LocalizedTitle = "Quartz", LocalizedContent = "deployment"
        });
        var deleted = Document("Quartz", "deployment", "deleted.md");
        deleted.IsDeleted = true;
        var excluded = Document("deployment", "Quartz", "excluded.md");
        excluded.Category = "hidden";
        db.Documents.AddRange(titleMatch, contentMatch, localizedMatch, deleted, excluded,
            Document("Unrelated", "No matching terms", "other.md"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = db.Documents.AsNoTracking().Where(document => document.Category == "visible");
        var first = await DocumentSearchService.SearchAsync(query, db, "Quartz deployment", 1, 2);
        var second = await DocumentSearchService.SearchAsync(query, db, "Quartz deployment", 2, 2);

        Assert.AreEqual(3, first.TotalCount);
        Assert.AreEqual(3, second.TotalCount);
        CollectionAssert.AreEqual(new[] { "localized.md", "title.md" }, first.Items.Select(document => document.FilePath).ToArray());
        Assert.AreEqual("content.md", second.Items.Single().FilePath);
        Assert.AreEqual("Quartz", first.Items[0].LocalizedDocuments.Single().LocalizedTitle);
    }

    private static Document Document(string title, string content, string path) => new()
    {
        Category = "visible",
        Title = title,
        Content = content,
        FilePath = path,
        SourceCulture = "en-US"
    };
}
