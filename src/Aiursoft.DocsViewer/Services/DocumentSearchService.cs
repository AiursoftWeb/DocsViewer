using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Aiursoft.DocsViewer.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Services;

/// <summary>
/// Weighted relevance search for documents.
///
/// Scoring weights (per matched term):
///   Exact document title match  → 1000
///   Document title prefix match → 100
///   Document title contains     → 10
///   Content contains            → 1
///
/// Single-term searches are fully translated to SQL (CASE WHEN scoring,
/// ORDER BY score, OFFSET/LIMIT pagination — no data pulled into memory).
/// Multi-term searches use SQL to pre-filter then score in memory.
/// </summary>
[ExcludeFromCodeCoverage]
public static class DocumentSearchService
{
    public static async Task<(List<Document> Items, int TotalCount)> SearchAsync(
        IQueryable<Document> baseQuery,
        DocsViewerDbContext db,
        string keyword,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var terms = SplitTerms(keyword);
        if (terms.Length == 0) return ([], 0);

        return terms.Length == 1
            ? await SingleTermSqlSearch(baseQuery, db, terms[0], page, pageSize, ct)
            : await MultiTermHybridSearch(baseQuery, db, terms, page, pageSize, ct);
    }

    /// <summary>
    /// Single-term path: scoring expression is fully pushed to SQL.
    /// </summary>
    private static async Task<(List<Document> Items, int TotalCount)> SingleTermSqlSearch(
        IQueryable<Document> baseQuery,
        DocsViewerDbContext db,
        string term,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var termLower = term.ToLower();
        var scoreQuery = baseQuery
            .Where(r => r.Title.Contains(term) || r.Content.Contains(term) ||
                        r.LocalizedDocuments.Any(ld => ld.LocalizedTitle.Contains(term) || ld.LocalizedContent.Contains(term)))
            .Select(r => new
            {
                Document = r,
                Score =
                    (r.Title.ToLower() == termLower ? 1000 : 0)
                    + (r.Title.StartsWith(term) ? 100 : 0)
                    + (r.Title.Contains(term) ? 10 : 0)
                    + (r.Content.Contains(term) ? 1 : 0)
                    + (r.LocalizedDocuments.Any(ld => ld.LocalizedTitle.ToLower() == termLower) ? 1000 : 0)
                    + (r.LocalizedDocuments.Any(ld => ld.LocalizedTitle.StartsWith(term)) ? 100 : 0)
                    + (r.LocalizedDocuments.Any(ld => ld.LocalizedTitle.Contains(term)) ? 10 : 0)
                    + (r.LocalizedDocuments.Any(ld => ld.LocalizedContent.Contains(term)) ? 1 : 0)
            });

        var ordered = scoreQuery
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => db.DocumentLikes.Count(l => l.DocumentId == x.Document.Id))
            .ThenByDescending(x => db.DocumentFavorites.Count(f => f.DocumentId == x.Document.Id))
            .ThenBy(x => x.Document.Title)
            .Select(x => x.Document);

        var total = await ordered.CountAsync(ct);
        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    /// <summary>
    /// Multi-term path: SQL filters candidates, then in-memory scoring.
    /// </summary>
    private static async Task<(List<Document> Items, int TotalCount)> MultiTermHybridSearch(
        IQueryable<Document> baseQuery,
        DocsViewerDbContext db,
        string[] terms,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var filtered = await baseQuery
            .Where(r => terms.Any(t => r.Title.Contains(t))
                     || terms.Any(t => r.Content.Contains(t))
                     || terms.Any(t => r.LocalizedDocuments.Any(ld => ld.LocalizedTitle.Contains(t)))
                     || terms.Any(t => r.LocalizedDocuments.Any(ld => ld.LocalizedContent.Contains(t))))
            .Select(r => new
            {
                Document = r,
                LikeCount = db.DocumentLikes.Count(l => l.DocumentId == r.Id),
                FavoriteCount = db.DocumentFavorites.Count(f => f.DocumentId == r.Id),
                r.LocalizedDocuments
            })
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var item in filtered)
        {
            item.Document.LocalizedDocuments = item.LocalizedDocuments.ToList();
        }

        var ordered = filtered
            .Select(x => (x.Document, x.LikeCount, x.FavoriteCount, Score: ComputeScore(x.Document, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.LikeCount)
            .ThenByDescending(x => x.FavoriteCount)
            .ThenBy(x => x.Document.Title)
            .Select(x => x.Document)
            .ToList();

        var total = ordered.Count;
        var items = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return (items, total);
    }

    private static int ComputeScore(Document r, string[] terms) =>
        terms.Sum(term =>
            (r.Title.Equals(term, StringComparison.OrdinalIgnoreCase) ? 1000 : 0)
            + (r.Title.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 100 : 0)
            + (r.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ? 10 : 0)
            + (r.Content.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            + (r.LocalizedDocuments.Any(ld => ld.LocalizedTitle.Equals(term, StringComparison.OrdinalIgnoreCase)) ? 1000 : 0)
            + (r.LocalizedDocuments.Any(ld => ld.LocalizedTitle.StartsWith(term, StringComparison.OrdinalIgnoreCase)) ? 100 : 0)
            + (r.LocalizedDocuments.Any(ld => ld.LocalizedTitle.Contains(term, StringComparison.OrdinalIgnoreCase)) ? 10 : 0)
            + (r.LocalizedDocuments.Any(ld => ld.LocalizedContent.Contains(term, StringComparison.OrdinalIgnoreCase)) ? 1 : 0));

    public static string[] SplitTerms(string keyword) =>
        Regex.Split(keyword.Trim(), @"\s+")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();
}
