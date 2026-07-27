using System.Security.Cryptography;
using System.Text;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Util;
using Aiursoft.Scanner.Abstractions;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Aiursoft.DocsViewer.Services;

/// <summary>
/// Semantic vector search for documents using an Ollama-hosted embedding model (bge-m3).
/// Computes cosine similarity against an in-memory cache of pre-computed document embeddings.
/// Caches query embeddings in the database (circular buffer) to avoid
/// redundant calls to the embedding model.
/// Falls back to classic keyword search when AI search is unavailable or times out.
/// </summary>
public class DocumentVectorSearchService(
    DocsViewerDbContext db,
    DocumentEmbeddingCache cache,
    GlobalSettingsService settingsService,
    IHttpClientFactory httpClientFactory,
    ILogger<DocumentVectorSearchService> logger) : IScopedDependency
{
    private const int EmbedTimeoutSeconds = 10;
    internal static readonly TimeSpan AccessThrottle = TimeSpan.FromHours(1);

    public async Task<(bool UsedAi, List<Document> Results, int TotalCount)> SearchAsync(
        IQueryable<Document> baseQuery,
        string query,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (!await settingsService.GetBoolSettingAsync(SettingsMap.EnableEmbeddingBasedSearch) ||
            !await settingsService.IsAiSearchEnabledAsync())
        {
            return (false, [], 0);
        }

        var snapshot = cache.Snapshot();
        if (snapshot.Count == 0)
        {
            return (false, [], 0);
        }

        float[]? queryVector;
        try
        {
            var expectedDimension = snapshot.Values.First().Length;
            queryVector = await EmbedQueryAsync(query, expectedDimension, ct);
        }
        catch
        {
            return (false, [], 0);
        }

        if (queryVector == null)
            return (false, [], 0);

        var scored = new List<(int DocumentId, float Score)>();
        var skippedDimensionMismatch = 0;
        foreach (var kv in snapshot)
        {
            if (kv.Value.Length != queryVector.Length)
            {
                skippedDimensionMismatch++;
                continue;
            }

            var score = EmbeddingHelper.CosineSimilarity(queryVector, kv.Value);
            if (score > 0)
            {
                scored.Add((kv.Key, score));
            }
        }

        if (scored.Count == 0 && skippedDimensionMismatch > 0)
        {
            logger.LogWarning(
                "Vector search skipped {Count} document embeddings because their dimensions did not match the query vector.",
                skippedDimensionMismatch);
            return (false, [], 0);
        }

        scored = scored
            .OrderByDescending(x => x.Score)
            .ToList();

        var total = scored.Count;
        var topIds = scored
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.DocumentId)
            .ToList();

        if (topIds.Count == 0)
        {
            return (true, [], total);
        }

        var documents = await baseQuery
            .Where(d => topIds.Contains(d.Id))
            .ToListAsync(ct);

        var docMap = documents.ToDictionary(d => d.Id);
        var ordered = topIds
            .Select(id => docMap.GetValueOrDefault(id))
            .Where(d => d != null)
            .Cast<Document>()
            .ToList();

        return (true, ordered, total);
    }

    public async Task<List<Document>> GetSimilarDocumentsAsync(
        IQueryable<Document> baseQuery,
        int documentId,
        int take,
        CancellationToken ct = default)
    {
        var snapshot = cache.Snapshot();
        if (!snapshot.TryGetValue(documentId, out var targetVector))
        {
            return [];
        }

        var topIds = snapshot
            .Where(kv => kv.Key != documentId)
            .Where(kv => kv.Value.Length == targetVector.Length)
            .Select(kv => (DocumentId: kv.Key, Score: EmbeddingHelper.CosineSimilarity(targetVector, kv.Value)))
            .OrderByDescending(x => x.Score)
            .Take(take)
            .Select(x => x.DocumentId)
            .ToList();

        if (topIds.Count == 0)
        {
            return [];
        }

        var documents = await baseQuery
            .Where(d => topIds.Contains(d.Id))
            .ToListAsync(ct);

        var docMap = documents.ToDictionary(d => d.Id);
        return topIds
            .Select(id => docMap.GetValueOrDefault(id))
            .Where(d => d != null)
            .Cast<Document>()
            .ToList();
    }

    private async Task<string> GetEmbeddingInstanceAsync()
    {
        var dedicated = await settingsService.GetSettingValueAsync(SettingsMap.EmbeddingOllamaInstance);
        if (!string.IsNullOrWhiteSpace(dedicated)) return dedicated;

        return await settingsService.GetSettingValueAsync(SettingsMap.OpenAiInstance);
    }

    private async Task<string> GetEmbeddingTokenAsync()
    {
        var dedicated = await settingsService.GetSettingValueAsync(SettingsMap.EmbeddingApiToken);
        if (!string.IsNullOrWhiteSpace(dedicated)) return dedicated;

        return await settingsService.GetSettingValueAsync(SettingsMap.OpenAiApiToken);
    }

    private static string ComputeQueryCacheKey(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(40);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
            if (sb.Length >= 40) break;
        }

        return sb.ToString();
    }

    private async Task<float[]?> EmbedQueryAsync(string text, int expectedDimension, CancellationToken ct)
    {
        var cacheKey = ComputeQueryCacheKey(text);

        var cached = await db.SearchEmbeddings
            .FirstOrDefaultAsync(e => e.QueryText == cacheKey, ct);

        if (cached != null)
        {
            var vector = EmbeddingHelper.Deserialize(cached.Embedding);
            if (vector != null && vector.Length == expectedDimension)
            {
                var now = DateTime.UtcNow;
                if (now - cached.LastAccessedAt >= AccessThrottle)
                {
                    cached.LastAccessedAt = now;
                    await db.SaveChangesAsync(ct);
                }

                return vector;
            }

            db.SearchEmbeddings.Remove(cached);
            await db.SaveChangesAsync(ct);
        }

        var instance = await GetEmbeddingInstanceAsync();
        var model = await settingsService.GetSettingValueAsync(SettingsMap.EmbeddingModel);
        var token = await GetEmbeddingTokenAsync();

        const int maxQueryChars = 8000;
        var input = text.Length > maxQueryChars ? text[..maxQueryChars] : text;

        var http = httpClientFactory.CreateClient();
        var baseUri = new Uri(instance);
        var embedEndpoint = $"{baseUri.Scheme}://{baseUri.Authority}/api/embed?keep_alive=-1";
        var requestBody = new { model, input };
        var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, embedEndpoint) { Content = content };
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(EmbedTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var response = await http.SendAsync(request, linkedCts.Token);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(linkedCts.Token);
        if (result?.Embeddings == null || result.Embeddings.Length == 0)
        {
            return null;
        }

        var embedding = result.Embeddings[0];
        EmbeddingHelper.Normalize(embedding);

        var serialized = EmbeddingHelper.Serialize(embedding);
        try
        {
            var now = DateTime.UtcNow;
            db.SearchEmbeddings.Add(new SearchEmbedding
            {
                QueryText = cacheKey,
                Embedding = serialized,
                CreatedAt = now,
                LastAccessedAt = now
            });
            await db.SaveChangesAsync(ct);

            await TrimCacheAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Failed to cache query embedding for '{Query}'. Likely a concurrent duplicate.", text);
        }

        return embedding;
    }

    private async Task TrimCacheAsync(CancellationToken ct)
    {
        var limit = await settingsService.GetIntSettingAsync(SettingsMap.EmbeddingQueryCacheLimit);
        if (limit <= 0) limit = 2000;

        var count = await db.SearchEmbeddings.CountAsync(ct);
        if (count <= limit) return;

        var toDelete = await db.SearchEmbeddings
            .OrderBy(e => e.LastAccessedAt)
            .Take(count - limit)
            .ToListAsync(ct);

        if (toDelete.Count > 0)
        {
            db.SearchEmbeddings.RemoveRange(toDelete);
            await db.SaveChangesAsync(ct);
        }
    }

    private class OllamaEmbedResponse
    {
        [JsonProperty("embeddings")]
        public float[][]? Embeddings { get; set; }
    }
}
