using Aiursoft.DocsViewer.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aiursoft.DocsViewer.Tests.IntegrationTests;

[TestClass]
public class DocumentsControllerTests : TestBase
{
    [TestMethod]
    public async Task GetDocumentDetail()
    {
        // Add a test document directly into the database.
        if (Server != null)
        {
            using var scope = Server.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DocsViewerDbContext>();
            if (!db.Documents.Any(d => d.FilePath == "test-doc.md"))
            {
                db.Documents.Add(new Document
                {
                    FilePath = "test-doc.md",
                    Content = "Test content",
                    Title = "Test Document",
                    Category = "root",
                    SourceCulture = "en-US",
                    FileLastModified = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
        }

        // Test the Details endpoint which triggers EF Core translation for path
        var url = "/test-doc.md";
        var response = await Http.GetAsync(url);
        
        // This validates that the request completes without crashing (500 Error)
        response.EnsureSuccessStatusCode();
    }
}
