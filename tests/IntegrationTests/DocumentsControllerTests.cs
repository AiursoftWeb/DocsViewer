using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Services;

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
                    Content = "# Test content\n\n## First section\n\nBody",
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
        var html = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "data-document-outline");
        StringAssert.Contains(html, "/scripts/document-outline.js");
    }

    [TestMethod]
    public async Task LimitedSearch_DoesNotClaimKeywordFallbackOrNoMatches()
    {
        await DisableVectorSearch();
        const string url = "/Documents/Search?q=quartz%20%26%20deployment&page=3";
        for (var i = 0; i < 8; i++)
        {
            using var response = await Http.GetAsync(url);
            response.EnsureSuccessStatusCode();
        }

        var content = await Http.GetStringAsync(url);

        StringAssert.Contains(content, "Search is temporarily limited. Please try again in a minute.");
        StringAssert.Contains(content, "Try again");
        StringAssert.Contains(content, "q=quartz%20%26%20deployment");
        StringAssert.Contains(content, "page=3");
        Assert.IsFalse(content.Contains("Showing keyword results"));
        Assert.IsFalse(content.Contains("Keyword Search"));
        Assert.IsFalse(content.Contains("Found 0 documents"));
        Assert.IsFalse(content.Contains("No documents matched your search"));
    }

    [TestMethod]
    public async Task PageBeyondSearchResults_OffersFirstPageWithoutClaimingNoMatches()
    {
        var term = $"PaginationFixture{Guid.NewGuid():N}";
        await DisableVectorSearch();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocsViewerDbContext>();
            for (var i = 0; i < 11; i++)
            {
                db.Documents.Add(new Document
                {
                    Title = $"Pagination guide {i}", FilePath = $"{term}-{i}.md", Category = "root", Content = term
                });
            }
            await db.SaveChangesAsync();
        }

        var content = await Http.GetStringAsync($"/Documents/Search?q={term}&page=99");

        StringAssert.Contains(content, "Found 11 documents matching your query.");
        StringAssert.Contains(content, "There are no documents on this page.");
        StringAssert.Contains(content, "Back to first page");
        StringAssert.Contains(content, "page=1");
        Assert.IsFalse(content.Contains("No documents matched your search"));
    }

    private async Task DisableVectorSearch()
    {
        using var scope = Server!.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(SettingsMap.EnableEmbeddingBasedSearch, "False");
    }
}
