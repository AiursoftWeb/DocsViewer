using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Models.HomeViewModels;
using Aiursoft.DocsViewer.Services;
using Aiursoft.WebTools.Attributes;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Aiursoft.DocsViewer.Controllers;

[LimitPerMin]
public class HomeController(
    DocsViewerDbContext db,
    IHostEnvironment env,
    GlobalSettingsService settingsService,
    DocumentMarkdownRenderer renderer,
    IStringLocalizer<HomeController> localizer) : Controller
{
    public async Task<IActionResult> Index()
    {
        var repoPath = Path.Combine(env.ContentRootPath, "App_Data", "DocsRepo");
        var docsHomePage = await settingsService.GetSettingValueAsync(SettingsMap.DocsHomePage);
        var docsRootPath = await settingsService.GetSettingValueAsync(SettingsMap.DocsRootPath);
        
        var homePagePhysicalPath = Path.GetFullPath(Path.Combine(repoPath, docsHomePage.TrimStart('/', '\\')));
        
        if (System.IO.File.Exists(homePagePhysicalPath))
        {
            var content = await System.IO.File.ReadAllTextAsync(homePagePhysicalPath);
            
            var baseDocPath = Path.GetFullPath(Path.Combine(repoPath, docsRootPath.TrimStart('/', '\\')));
            Document? dbDoc = null;
            if (homePagePhysicalPath.StartsWith(baseDocPath, StringComparison.OrdinalIgnoreCase))
            {
                var relativeFilePath = Path.GetRelativePath(baseDocPath, homePagePhysicalPath).Replace('\\', '/');
                dbDoc = await db.Documents.FirstOrDefaultAsync(d => d.FilePath.ToLower() == relativeFilePath.ToLower());
            }

            if (dbDoc != null)
            {
            var currentCulture = HttpContext.Features.Get<Microsoft.AspNetCore.Localization.IRequestCultureFeature>()
                ?.RequestCulture.Culture.Name ?? string.Empty;
            var localized = await db.LocalizedDocuments
                .FirstOrDefaultAsync(lr => lr.DocumentId == dbDoc.Id && lr.Culture == currentCulture);

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

        return this.StackView(new IndexViewModel
        {
            HasReadme = false,
            ReadmeHtml = localizer["The configured home page ({0}) was not found in the repository. Please check the Docs Home Page setting.", docsHomePage].Value
        });
    }
}
