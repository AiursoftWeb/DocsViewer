using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Services;

namespace Aiursoft.DocsViewer.Tests.IntegrationTests;

[TestClass]
public class MoreDocsViewerTests : TestBase
{
    [TestMethod]
    public async Task TestDocumentsIndexVariants()
    {
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocsViewerDbContext>();
            if (!db.Documents.Any(d => d.FilePath == "index-test.md"))
            {
                db.Documents.Add(new Document
                {
                    FilePath = "index-test.md",
                    Content = "# Heading\n\nContent",
                    Title = "Index Test Doc",
                    Category = "sample",
                    SourceCulture = "en-US",
                    FileLastModified = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var contributorService = scope.ServiceProvider.GetRequiredService<DocumentContributorService>();
            var contributors = await contributorService.GetContributorsAsync("index-test.md");
            Assert.IsNotNull(contributors);
        }

        var urls = new[]
        {
            "/Documents",
            "/Documents/Index",
            "/Documents?category=sample",
            "/Documents?sortBy=likes",
            "/Documents?sortBy=least-liked",
            "/Documents?sortBy=comments",
            "/Documents?sortBy=least-commented",
            "/Documents?sortBy=favorites",
            "/Documents?sortBy=least-favorited"
        };

        foreach (var url in urls)
        {
            var response = await Http.GetAsync(url);
            Assert.IsTrue(response.IsSuccessStatusCode, $"Failed GET on {url} with status {response.StatusCode}");
        }
    }

    [TestMethod]
    public async Task TestAdminPages()
    {
        await LoginAsAdmin();

        var pages = new[]
        {
            "/Users",
            "/Roles",
            "/Permissions",
            "/Jobs",
            "/GlobalSettings",
            "/CommentManagement"
        };

        foreach (var page in pages)
        {
            var response = await Http.GetAsync(page);
            Assert.IsTrue(response.IsSuccessStatusCode, $"Failed GET on {page} with status {response.StatusCode}");
        }
    }
}
