using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Models.DocumentsViewModels;
using Aiursoft.DocsViewer.Services;
using Aiursoft.WebTools.Attributes;
using Markdig;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Localization;

namespace Aiursoft.DocsViewer.Controllers;

[LimitPerMin]
public class DocumentsController(
    DocsViewerDbContext db,
    UserManager<User> userManager,
    GlobalSettingsService globalSettingsService,
    DocumentLocalizationService documentLocalization,
    DocumentEmbeddingCache embeddingCache,
    DocumentVectorSearchService vectorSearchService,
    SearchRateLimiter rateLimiter,
    DocumentMarkdownRenderer renderer,
    IStringLocalizer<DocumentsController> localizer) : Controller
{
    [ExcludeFromCodeCoverage]
    // ReSharper disable once UnusedMember.Local
    private void _useless_for_localizer()
    {
        _ = localizer["All Documents"];
        _ = localizer["Most Liked"];
        _ = localizer["Least Liked"];
        _ = localizer["Most Commented"];
        _ = localizer["Least Commented"];
        _ = localizer["Most Favorited"];
        _ = localizer["Least Favorited"];
    }

    public async Task<IActionResult> Index(string? category, string? sortBy)
    {
        var baseQuery = BuildQuery(category, sortBy);
        var totalCount = await baseQuery.CountAsync();
        var documents = await baseQuery
            .Take(IndexViewModel.PageSize)
            .ToListAsync();

        var displayName = GetDisplayName(category, sortBy);
        var (localizedTitles, localizedContents) = await documentLocalization.LoadLocalizedStringsAsync(documents);

        return this.StackView(new IndexViewModel
        {
            PageTitle = displayName,
            Category = category,
            SortBy = sortBy,
            CategoryDisplayName = displayName,
            Documents = documents,
            TotalCount = totalCount,
            HasMore = totalCount > IndexViewModel.PageSize,
            LikeCounts = await LoadLikeCountsAsync(documents),
            LocalizedTitles = localizedTitles,
            LocalizedContents = localizedContents
        });
    }

    [HttpGet]
    public async Task<IActionResult> LoadMore(string? category, string? sortBy, int page = 2)
    {
        page = Math.Max(2, page);
        var baseQuery = BuildQuery(category, sortBy);
        var totalCount = await baseQuery.CountAsync();
        var documents = await baseQuery
            .Skip((page - 1) * IndexViewModel.PageSize)
            .Take(IndexViewModel.PageSize)
            .ToListAsync();

        var hasMore = page * IndexViewModel.PageSize < totalCount;
        Response.Headers["X-Has-More"] = hasMore ? "true" : "false";
        Response.Headers["X-Next-Page"] = (page + 1).ToString();

        var (localizedTitles, localizedContents) = await documentLocalization.LoadLocalizedStringsAsync(documents);
        return PartialView("_DocumentCards", new DocumentCardsViewModel
        {
            Documents = documents,
            LikeCounts = await LoadLikeCountsAsync(documents),
            LocalizedTitles = localizedTitles,
            LocalizedContents = localizedContents
        });
    }

    private IQueryable<Document> BuildQuery(string? category, string? sortBy)
    {
        var query = db.Documents.AsQueryable();

        if (!string.IsNullOrEmpty(category))
            query = query.Where(r => r.Category == category);

        IOrderedQueryable<Document> ordered = sortBy switch
        {
            "likes_desc" => query.OrderByDescending(r => db.DocumentLikes.Count(l => l.DocumentId == r.Id)),
            "likes_asc" => query.OrderBy(r => db.DocumentLikes.Count(l => l.DocumentId == r.Id)),
            "comments_desc" => query.OrderByDescending(r => db.DocumentComments.Count(c => c.DocumentId == r.Id)),
            "comments_asc" => query.OrderBy(r => db.DocumentComments.Count(c => c.DocumentId == r.Id)),
            "favorites_desc" => query.OrderByDescending(r => db.DocumentFavorites.Count(f => f.DocumentId == r.Id)),
            "favorites_asc" => query.OrderBy(r => db.DocumentFavorites.Count(f => f.DocumentId == r.Id)),
            _ => query.OrderBy(r => r.Title)
        };

        if (string.IsNullOrEmpty(sortBy))
        {
            return ordered
                .ThenByDescending(r => db.DocumentLikes.Count(l => l.DocumentId == r.Id))
                .ThenByDescending(r => db.DocumentFavorites.Count(f => f.DocumentId == r.Id));
        }

        return ordered
            .ThenByDescending(r => db.DocumentLikes.Count(l => l.DocumentId == r.Id))
            .ThenByDescending(r => db.DocumentFavorites.Count(f => f.DocumentId == r.Id))
            .ThenBy(r => r.Title);
    }

    private string GetDisplayName(string? category, string? sortBy) =>
        sortBy switch
        {
            "likes_desc" => localizer["Most Liked"].Value,
            "likes_asc" => localizer["Least Liked"].Value,
            "comments_desc" => localizer["Most Commented"].Value,
            "comments_asc" => localizer["Least Commented"].Value,
            "favorites_desc" => localizer["Most Favorited"].Value,
            "favorites_asc" => localizer["Least Favorited"].Value,
            _ => string.IsNullOrEmpty(category)
                    ? localizer["All Documents"].Value
                    : category
        };

    private async Task<Dictionary<int, int>> LoadLikeCountsAsync(List<Document> documents)
    {
        if (documents.Count == 0) return [];
        var ids = documents.Select(r => r.Id).ToList();
        return await db.DocumentLikes
            .Where(l => ids.Contains(l.DocumentId))
            .GroupBy(l => l.DocumentId)
            .Select(g => new { DocumentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DocumentId, x => x.Count);
    }

    public async Task<IActionResult> Random(string? category, string? sortBy)
    {
        var query = db.Documents.AsQueryable();

        if (!string.IsNullOrEmpty(category))
            query = query.Where(r => r.Category == category);

        var ids = await query.Select(r => r.Id).ToListAsync();
        if (ids.Count == 0)
            return RedirectToAction(nameof(Index), new { category, sortBy });

        var randomId = ids[System.Random.Shared.Next(ids.Count)];
        return RedirectToAction(nameof(DetailById), new { id = randomId });
    }

    public async Task<IActionResult> DetailById(int id)
    {
        var doc = await db.Documents
            .FirstOrDefaultAsync(r => r.Id == id);

        if (doc == null)
            return NotFound();

        var htmlPath = doc.FilePath[..^3].Replace('\\', '/') + ".html";
        return Redirect($"/{htmlPath}");
    }

    public async Task<IActionResult> Detail(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var mdPath = path[..^5] + ".md";
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.FilePath.ToLower().Replace('\\', '/') == mdPath.ToLower().Replace('\\', '/'));
        
        if (doc == null)
        {
            return NotFound();
        }

        var id = doc.Id;
        var currentCulture = HttpContext.Features.Get<Microsoft.AspNetCore.Localization.IRequestCultureFeature>()
            ?.RequestCulture.Culture.Name ?? string.Empty;
        var localized = await db.LocalizedDocuments
            .FirstOrDefaultAsync(lr => lr.DocumentId == id && lr.Culture == currentCulture);

        var userId = userManager.GetUserId(User);
        var isFavorited = userId != null &&
            await db.DocumentFavorites.AnyAsync(f => f.UserId == userId && f.DocumentId == id);
        var isLiked = userId != null &&
            await db.DocumentLikes.AnyAsync(l => l.UserId == userId && l.DocumentId == id);
        var likeCount = await db.DocumentLikes.CountAsync(l => l.DocumentId == id);

        var comments = await db.DocumentComments
            .Where(c => c.DocumentId == id && c.ParentCommentId == null)
            .Include(c => c.User)
            .Include(c => c.Replies).ThenInclude(r => r.User)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

        var markdown = localized?.LocalizedContent ?? doc.Content;
        var title = localized?.LocalizedTitle ?? doc.Title;
        
        var html = renderer.RenderHtml(markdown);

        var repoUrl = await globalSettingsService.GetSettingValueAsync(SettingsMap.DocsRepoUrl);
        var repoWebUrl = repoUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repoUrl[..^4]
            : repoUrl;
        
        var docsRoot = await globalSettingsService.GetSettingValueAsync(SettingsMap.DocsRootPath);
        var docsRootPrefix = string.IsNullOrWhiteSpace(docsRoot) ? "" : $"{docsRoot.Trim('/')}/";
        var gitHubEditUrl = string.IsNullOrWhiteSpace(repoUrl) ? null : $"{repoWebUrl}/edit/main/{docsRootPrefix}{doc.FilePath.Replace('\\', '/')}";
        var gitHubHistoryUrl = string.IsNullOrWhiteSpace(repoUrl) ? null : $"{repoWebUrl}/commits/main/{docsRootPrefix}{doc.FilePath.Replace('\\', '/')}";

        return this.StackView(new DetailViewModel
        {
            PageTitle = title,
            Document = doc,
            RenderedMarkdown = html,
            IsFavorited = isFavorited,
            IsLiked = isLiked,
            LikeCount = likeCount,
            Comments = comments,
            GitHubEditUrl = gitHubEditUrl,
            GitHubHistoryUrl = gitHubHistoryUrl,
            CategoryDisplayName = GetDisplayName(doc.Category, null),
            LocalizedDocument = localized,
            ShowSimilarDocumentsButton = embeddingCache.Count > 0 && embeddingCache.Count >= await db.Documents.CountAsync()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Similar(int id)
    {
        var sourceDoc = await db.Documents
            .FirstOrDefaultAsync(r => r.Id == id);

        if (sourceDoc == null)
            return NotFound();

        var documents = await vectorSearchService.GetSimilarDocumentsAsync(db.Documents, id, 20);
        var (localizedTitles, localizedContents) = await documentLocalization.LoadLocalizedStringsAsync(documents);

        return this.StackView(new SimilarViewModel
        {
            SourceDocument = sourceDoc,
            CategoryDisplayName = GetDisplayName(sourceDoc.Category, null),
            SimilarDocuments = documents,
            LikeCounts = await LoadLikeCountsAsync(documents),
            LocalizedTitles = localizedTitles,
            LocalizedContents = localizedContents
        });
    }

    [HttpGet]
    public async Task<IActionResult> Search(string? keyword, int page = 1)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return RedirectToAction(nameof(Index));

        page = Math.Max(1, page);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!rateLimiter.TryConsume(ip))
        {
            ModelState.AddModelError("", "Rate limit exceeded. Try again in a minute.");
            return this.StackView(new SearchViewModel
            {
                Keyword = keyword,
                Page = page,
                Documents = [],
                TotalCount = 0,
                LikeCounts = [],
                LocalizedTitles = [],
                LocalizedContents = []
            });
        }

        var (usedAi, results, total) = await vectorSearchService.SearchAsync(
            db.Documents, keyword, page, SearchViewModel.PageSize);

        if (!usedAi && results.Count == 0)
        {
            var searchRes = await DocumentSearchService.SearchAsync(
                db.Documents, db, keyword, page, SearchViewModel.PageSize);
            results = searchRes.Items;
            total = searchRes.TotalCount;
        }

        var (localizedTitles, localizedContents) = await documentLocalization.LoadLocalizedStringsAsync(results);

        return this.StackView(new SearchViewModel
        {
            Keyword = keyword,
            Page = page,
            TotalCount = total,
            Documents = results,
            UsedAi = usedAi,
            LikeCounts = await LoadLikeCountsAsync(results),
            LocalizedTitles = localizedTitles,
            LocalizedContents = localizedContents
        });
    }
}
