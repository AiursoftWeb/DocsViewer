using System.Diagnostics.CodeAnalysis;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Util;
using Microsoft.EntityFrameworkCore;

using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.DocsViewer.Services;

/// <summary>
/// In-memory cache of document embedding vectors for fast cosine-similarity search.
/// Loaded at startup and refreshed periodically via RefreshEmbeddingCacheJob.
/// </summary>
[ExcludeFromCodeCoverage]
public class DocumentEmbeddingCache(ILogger<DocumentEmbeddingCache> logger) : ISingletonDependency
{
    private Dictionary<int, float[]> _cache = [];
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    /// <summary>Returns a snapshot of the current cache for search.</summary>
    public Dictionary<int, float[]> Snapshot()
    {
        lock (_lock) return new Dictionary<int, float[]>(_cache);
    }

    public async Task LoadAsync(DocsViewerDbContext db)
    {
        const int maxEntries = 10_000;

        var embeddings = await db.Documents
            .AsNoTracking()
            .Where(r => r.Embedding != null)
            .OrderByDescending(r => r.LastEmbeddedAt)
            .Select(r => new { r.Id, r.Embedding })
            .Take(maxEntries + 1) // +1 to detect overflow
            .ToListAsync();

        if (embeddings.Count > maxEntries)
        {
            logger.LogWarning(
                "DocumentEmbeddingCache: {Count} documents have embeddings but cache is capped at {Max}. Only the {Max} most recently embedded documents are loaded. Consider increasing the limit or archiving old documents.",
                embeddings.Count, maxEntries, maxEntries);
            embeddings = embeddings.Take(maxEntries).ToList();
        }

        var newCache = new Dictionary<int, float[]>();
        foreach (var item in embeddings)
        {
            var vector = EmbeddingHelper.Deserialize(item.Embedding!);
            if (vector != null)
            {
                newCache[item.Id] = vector;
            }
            else
            {
                logger.LogWarning("Failed to deserialize embedding for document {DocumentId}: byte length {Length} is not a multiple of 4.",
                    item.Id, item.Embedding!.Length);
            }
        }

        lock (_lock)
        {
            _cache = newCache;
        }
    }
}
