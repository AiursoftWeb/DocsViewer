using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

public class RefreshEmbeddingCacheJob(
    DocsViewerDbContext db,
    DocumentEmbeddingCache cache,
    GlobalSettingsService settingsService,
    ILogger<RefreshEmbeddingCacheJob> logger) : IBackgroundJob
{
    public string Name => "Refresh Embedding Cache";

    public string Description => "Reloads the document embedding vectors into memory.";

    public async Task ExecuteAsync()
    {
        var useAiSearch = await settingsService.GetBoolSettingAsync(SettingsMap.EnableEmbeddingBasedSearch);
        if (!useAiSearch)
        {
            logger.LogInformation("RefreshEmbeddingCacheJob: EnableEmbeddingBasedSearch is disabled. Skipping.");
            return;
        }

        logger.LogInformation("RefreshEmbeddingCacheJob started.");
        await cache.LoadAsync(db);
        logger.LogInformation("RefreshEmbeddingCacheJob completed. Cache contains {Count} vectors.", cache.Count);
    }
}
