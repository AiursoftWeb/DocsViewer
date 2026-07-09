using System.Text;
using Aiursoft.Canon;
using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Util;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

/// <summary>
/// Generates embedding vectors for documents using the configured Ollama embedding model.
/// Processes documents whose FileLastModified is newer than LastEmbeddedAt.
/// Uses cursor-based batching (10 per batch), head+tail truncation, binary-search fallback,
/// and RetryEngine for transient failures.
/// </summary>
public class GenerateEmbeddingsJob(
    DocsViewerDbContext db,
    GlobalSettingsService settingsService,
    IHttpClientFactory httpClientFactory,
    RetryEngine retryEngine,
    ILogger<GenerateEmbeddingsJob> logger) : IBackgroundJob
{
    public string Name => "Generate Document Embeddings";

    public string Description =>
        "Generates 1024-dimension embedding vectors for documents using the configured Ollama embedding model.";

    public async Task ExecuteAsync()
    {
        if (!await settingsService.IsAiSearchEnabledAsync())
        {
            logger.LogInformation("GenerateEmbeddingsJob: Ollama endpoint not configured. Skipping.");
            return;
        }

        var useAiSearch = await settingsService.GetBoolSettingAsync(SettingsMap.EnableEmbeddingBasedSearch);
        if (!useAiSearch)
        {
            logger.LogInformation("GenerateEmbeddingsJob: EnableEmbeddingBasedSearch is disabled. Skipping.");
            return;
        }

        var model = await settingsService.GetSettingValueAsync(SettingsMap.EmbeddingModel);
        if (string.IsNullOrWhiteSpace(model))
        {
            logger.LogInformation("GenerateEmbeddingsJob: EmbeddingModel not configured. Skipping.");
            return;
        }

        var instance = await GetEmbeddingInstanceAsync();
        var token = await GetEmbeddingTokenAsync();

        var lastId = 0;
        while (true)
        {
            var currentLastId = lastId;
            var pendingDocs = await db.Documents
                .Where(d => d.Id > currentLastId && d.LastEmbeddedAt < d.FileLastModified)
                .OrderBy(d => d.Id)
                .Take(10)
                .ToListAsync();

            if (pendingDocs.Count == 0) break;

            foreach (var doc in pendingDocs)
            {
                try
                {
                    await retryEngine.RunWithRetry(async _ =>
                    {
                        var embedding = await CallEmbedApiAsync(instance, model, token, doc);
                        doc.Embedding = EmbeddingHelper.Serialize(embedding);
                        doc.LastEmbeddedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "GenerateEmbeddingsJob: Failed to generate embedding for document '{Title}'.", doc.Title);
                }
            }

            lastId = pendingDocs.Max(d => d.Id);
        }
    }

    private async Task<float[]> CallEmbedApiAsync(string instance, string model, string token, Document doc)
    {
        var text = BuildDocumentText(doc);
        var http = httpClientFactory.CreateClient();

        var baseUri = new Uri(instance);
        var embedEndpoint = $"{baseUri.Scheme}://{baseUri.Authority}/api/embed?keep_alive=-1";

        // bge-m3 has an 8192-token context window. Characters map to tokens at different
        // rates per language (CJK ≈ 1:1, English ≈ 1:4). Start with 8000 chars (safe for
        // all languages) and use binary-search fallback if Ollama still reports the input
        // is too long.
        var maxChars = 8000;
        while (true)
        {
            var input = TruncateForEmbedding(text, maxChars);

            // num_gpu=0 forces CPU-only inference so the embedding model never competes with the LLM for VRAM.
            var requestBody = new { model, input, options = new { num_gpu = -1 } };
            var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, embedEndpoint) { Content = content };
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var response = await http.SendAsync(request, timeoutCts.Token);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>();
                if (result?.Embeddings == null || result.Embeddings.Length == 0)
                {
                    throw new Exception($"Ollama returned no embeddings for document '{doc.Title}'.");
                }

                var vector = result.Embeddings[0];
                EmbeddingHelper.Normalize(vector);
                return vector;
            }

            // If the input is too long, halve the limit and retry. Otherwise fail.
            var errorBody = await response.Content.ReadAsStringAsync();
            var isContextError = errorBody.Contains("context", StringComparison.OrdinalIgnoreCase) ||
                                 errorBody.Contains("length", StringComparison.OrdinalIgnoreCase) ||
                                 errorBody.Contains("exceed", StringComparison.OrdinalIgnoreCase);
            if (!isContextError || maxChars <= 500)
            {
                throw new HttpRequestException(
                    $"Ollama embedding request failed for '{doc.Title}' (HTTP {(int)response.StatusCode}): {errorBody}");
            }

            var prev = maxChars;
            maxChars /= 2;
            logger.LogWarning(
                "Embedding input for '{Title}' still too long at {Prev} chars, retrying with {Current} chars (binary fallback).",
                doc.Title, prev, maxChars);
        }
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

    /// <summary>
    /// Truncates text to fit within bge-m3's 8192-token context window.
    /// Uses head+tail preservation: keeps the first 75% and last ~25% of the budget
    /// so both the introduction and conclusion contribute to the embedding.
    /// </summary>
    internal static string TruncateForEmbedding(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;

        var head = (int)(maxChars * 0.75);
        var tail = maxChars - head - 5; // 5 for "\n...\n" separator
        if (tail <= 0) return text[..maxChars];

        return string.Concat(text.AsSpan(0, head), "\n...\n", text.AsSpan(text.Length - tail));
    }

    private static string BuildDocumentText(Document doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(doc.Title);
        if (!string.IsNullOrWhiteSpace(doc.Content))
            sb.Append(doc.Content);
        return sb.ToString();
    }

    private class OllamaEmbedResponse
    {
        [JsonProperty("embeddings")]
        public float[][]? Embeddings { get; set; }
    }
}
