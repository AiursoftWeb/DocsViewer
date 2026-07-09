using System.Text;
using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Util;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

public class GenerateEmbeddingsJob(
    DocsViewerDbContext db,
    GlobalSettingsService settingsService,
    IHttpClientFactory httpClientFactory,
    ILogger<GenerateEmbeddingsJob> logger) : IBackgroundJob
{
    public string Name => "Generate Embeddings";

    public string Description => "Generates vector embeddings for updated documents.";

    public async Task ExecuteAsync()
    {
        if (!await settingsService.IsAiSearchEnabledAsync())
        {
            return;
        }

        var documents = await db.Documents
            .Where(d => d.Embedding == null || d.LastEmbeddedAt < d.FileLastModified)
            .ToListAsync();

        if (documents.Count == 0)
        {
            return;
        }

        var instance = await GetEmbeddingInstanceAsync();
        var model = await settingsService.GetSettingValueAsync(SettingsMap.EmbeddingModel);
        var token = await GetEmbeddingTokenAsync();

        var http = httpClientFactory.CreateClient();
        var baseUri = new Uri(instance);
        var embedEndpoint = $"{baseUri.Scheme}://{baseUri.Authority}/api/embed?keep_alive=-1";

        foreach (var d in documents)
        {
            try
            {
                var text = $"{d.Title}\n\n{d.Content}";
                const int maxQueryChars = 8000;
                var input = text.Length > maxQueryChars ? text[..maxQueryChars] : text;

                var requestBody = new { model, input, options = new { num_gpu = 0 } };
                var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, embedEndpoint) { Content = content };
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                var response = await http.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>();
                if (result?.Embeddings != null && result.Embeddings.Length > 0)
                {
                    var embedding = result.Embeddings[0];
                    EmbeddingHelper.Normalize(embedding);
                    d.Embedding = EmbeddingHelper.Serialize(embedding);
                    d.LastEmbeddedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to generate embedding for document {DocumentId}.", d.Id);
            }
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

    private class OllamaEmbedResponse
    {
        [JsonProperty("embeddings")]
        public float[][]? Embeddings { get; set; }
    }
}
