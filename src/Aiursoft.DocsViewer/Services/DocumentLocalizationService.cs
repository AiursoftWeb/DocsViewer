using Aiursoft.DocsViewer.Entities;
using Aiursoft.Scanner.Abstractions;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Services;

/// <summary>
/// Resolves AI-translated document title/content strings for the current request culture.
/// </summary>
public class DocumentLocalizationService(
    DocsViewerDbContext db,
    IHttpContextAccessor httpContextAccessor) : IScopedDependency
{
    public async Task<(Dictionary<int, string> Titles, Dictionary<int, string> Contents)>
        LoadLocalizedStringsAsync(IEnumerable<Document> documents)
    {
        var list = documents as List<Document> ?? documents.ToList();
        if (list.Count == 0) return ([], []);

        var culture = httpContextAccessor.HttpContext?.Features
            .Get<IRequestCultureFeature>()
            ?.RequestCulture.Culture.Name ?? string.Empty;
        if (string.IsNullOrEmpty(culture)) return ([], []);

        var ids = list.Select(d => d.Id).ToList();
        var rows = await db.LocalizedDocuments
            .Where(ld => ids.Contains(ld.DocumentId) && ld.Culture == culture)
            .Select(ld => new { ld.DocumentId, ld.LocalizedTitle, ld.LocalizedContent })
            .ToListAsync();

        var titles = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.LocalizedTitle))
            .ToDictionary(r => r.DocumentId, r => r.LocalizedTitle);
        var contents = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.LocalizedContent))
            .ToDictionary(r => r.DocumentId, r => r.LocalizedContent);
        return (titles, contents);
    }
}
