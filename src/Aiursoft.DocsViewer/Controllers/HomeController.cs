using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Models.HomeViewModels;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Services.FileStorage;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.DocsViewer.Controllers;

[LimitPerMin]
public class HomeController(
    DocsViewerDbContext db,
    StorageRootPathProvider storageRootPathProvider,
    DocumentMarkdownRenderer renderer,
    NavConfigParser navConfigParser,
    IStringLocalizer<HomeController> localizer) : Controller
{
    public async Task<IActionResult> Index()
    {
        var repoPath = Path.Combine(storageRootPathProvider.GetStorageRootPath(), "repo");
        var navConfig = await navConfigParser.ParseAsync(repoPath);

        if (navConfig?.HomePage != null)
        {
            // Resolve: docsDir + homePage path relative to repo
            var homePagePath = Path.Combine(repoPath, navConfig.DocsDir, navConfig.HomePage);
            if (System.IO.File.Exists(homePagePath))
            {
                var content = await System.IO.File.ReadAllTextAsync(homePagePath);

                var relativePath = navConfig.HomePage.Replace('\\', '/').ToLower();
                var dbDoc = await db.Documents
                    .FirstOrDefaultAsync(d => d.FilePath.ToLower() == relativePath);

                if (dbDoc != null)
                {
                    var currentCulture = HttpContext.Features
                        .Get<Microsoft.AspNetCore.Localization.IRequestCultureFeature>()
                        ?.RequestCulture.Culture.Name ?? string.Empty;
                    var isSourceCulture = dbDoc.SourceCulture != null &&
                        string.Equals(dbDoc.SourceCulture, currentCulture, StringComparison.OrdinalIgnoreCase);
                    var localized = isSourceCulture
                        ? null
                        : await db.LocalizedDocuments
                            .FirstOrDefaultAsync(ld => ld.DocumentId == dbDoc.Id && ld.Culture == currentCulture);
                    content = localized?.LocalizedContent ?? dbDoc.Content;
                }

                var html = renderer.RenderHtml(content);
                return this.StackView(new IndexViewModel
                {
                    HasReadme = true,
                    ReadmeHtml = html,
                    DocumentId = dbDoc?.Id
                });
            }
        }

        return this.StackView(new IndexViewModel
        {
            HasReadme = false,
            ReadmeHtml = localizer["No properdocs.yml found, or the first document could not be located. Please check your repository configuration."].Value
        });
    }
}
