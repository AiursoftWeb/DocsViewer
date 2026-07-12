using Aiursoft.DocsViewer.Entities;
using Aiursoft.Scanner.Abstractions;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Services;

/// <summary>
/// Resolves AI-translated sidebar navigation group/folder labels for the current request culture.
///
/// This is the read side of the nav-title localization feature; the write side is
/// <see cref="BackgroundJobs.LocalizeNavTitlesJob"/>, which populates the
/// <see cref="LocalizedNavTitle"/> key/value table.
/// </summary>
public class NavTitleLocalizationService(
    DocsViewerDbContext db,
    IHttpContextAccessor httpContextAccessor) : IScopedDependency
{
    /// <summary>
    /// Loads a <c>SourceText → LocalizedText</c> map for the given nav labels in the current culture.
    /// Labels without a translation are simply absent from the dictionary, so callers should fall
    /// back to the original text (e.g. <c>map.GetValueOrDefault(name, name)</c>).
    /// </summary>
    public async Task<Dictionary<string, string>> LoadLocalizedNavTitlesAsync(IEnumerable<string> sourceTexts)
    {
        var distinct = sourceTexts
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
        if (distinct.Count == 0) return [];

        var culture = httpContextAccessor.HttpContext?.Features
            .Get<IRequestCultureFeature>()
            ?.RequestCulture.Culture.Name ?? string.Empty;
        if (string.IsNullOrEmpty(culture)) return [];

        var rows = await db.LocalizedNavTitles
            .AsNoTracking()
            .Where(nt => distinct.Contains(nt.SourceText) && nt.Culture == culture)
            .Select(nt => new { nt.SourceText, nt.LocalizedText })
            .ToListAsync();

        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.LocalizedText))
            .GroupBy(r => r.SourceText)
            .ToDictionary(g => g.Key, g => g.First().LocalizedText);
    }
}
