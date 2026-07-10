using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

/// <summary>
/// Periodically translates document content into the configured languages using an AI endpoint.
/// Runs until all pending (document, culture) pairs are translated, saving progress along the way.
/// Skips documents whose <see cref="LocalizedDocument.LastLocalizedAt"/> is already up-to-date.
/// </summary>
public class LocalizeDocumentsJob(
    DocsViewerDbContext dbContext,
    GlobalSettingsService globalSettingsService,
    IDocumentTranslationService documentTranslationService,
    ILogger<LocalizeDocumentsJob> logger) : IBackgroundJob
{
    public string Name => "Localize Documents";

    public string Description =>
        "Translates document content into configured languages using an AI endpoint (Ollama / OpenAI-compatible).";

    public async Task ExecuteAsync()
    {
        if (!await globalSettingsService.IsAiLocalizationEnabledAsync())
        {
            logger.LogInformation("LocalizeDocumentsJob: AI endpoint not configured. Skipping.");
            return;
        }

        var languagesRaw = await globalSettingsService.GetSettingValueAsync(SettingsMap.LocalizationLanguages);
        var cultures = languagesRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (cultures.Length == 0)
        {
            logger.LogInformation("LocalizeDocumentsJob: No target languages configured. Skipping.");
            return;
        }

        logger.LogInformation("LocalizeDocumentsJob: starting with {Count} target languages: {Languages}",
            cultures.Length, string.Join(", ", cultures));

        var totalProcessed = 0;

        foreach (var culture in cultures)
        {
            var lastId = 0;
            while (true)
            {
                var currentLastId = lastId;
                var pendingDocs = await dbContext.Documents
                    .Where(d => d.Id > currentLastId && !dbContext.LocalizedDocuments.Any(ld =>
                        ld.DocumentId == d.Id &&
                        ld.Culture == culture &&
                        ld.LastLocalizedAt >= d.FileLastModified))
                    .OrderBy(d => d.Id)
                    .Take(20)
                    .ToListAsync();

                if (pendingDocs.Count == 0) break;

                foreach (var document in pendingDocs)
                {
                    var success = await LocalizeDocumentAsync(document, culture);
                    if (success)
                    {
                        totalProcessed++;
                        // Save immediately after each document to ensure progress survives a crash
                        await dbContext.SaveChangesAsync();
                    }
                }

                lastId = pendingDocs.Max(d => d.Id);
                logger.LogInformation(
                    "LocalizeDocumentsJob: [{Culture}] batch finished. Last ID: {LastId}. Total processed: {Total}.",
                    culture, lastId, totalProcessed);
            }

            logger.LogInformation("LocalizeDocumentsJob: [{Culture}] all documents up-to-date.", culture);
        }

        logger.LogInformation("LocalizeDocumentsJob: done. Processed {Count} document/language pair(s).", totalProcessed);
    }

    private async Task<bool> LocalizeDocumentAsync(Document document, string culture)
    {
        // Ensure a row exists so partial progress is never lost.
        var row = await dbContext.LocalizedDocuments
            .FirstOrDefaultAsync(ld => ld.DocumentId == document.Id && ld.Culture == culture);

        if (row == null)
        {
            row = new LocalizedDocument
            {
                DocumentId = document.Id,
                Culture = culture,
                LastLocalizedAt = DateTime.MinValue // not yet complete
            };
            dbContext.LocalizedDocuments.Add(row);
            await dbContext.SaveChangesAsync();
        }

        // If the source document has been updated since the last localization,
        // clear all fields so they will be re-translated.
        if (row.LastLocalizedAt < document.FileLastModified)
        {
            row.LocalizedTitle = string.Empty;
            row.LocalizedContent = string.Empty;
        }

        logger.LogInformation(
            "LocalizeDocumentsJob: translating document '{Title}' (id={Id}) to {Culture}.",
            document.Title, document.Id, culture);

        // Translate each field sequentially — save after each success.
        if (string.IsNullOrWhiteSpace(row.LocalizedTitle))
            await TranslateAndSaveAsync(document.Title, v => row.LocalizedTitle = v, culture);
        if (string.IsNullOrWhiteSpace(row.LocalizedContent))
            await TranslateAndSaveAsync(document.Content, v => row.LocalizedContent = v, culture);

        // Mark complete only when every field has content.
        if (!string.IsNullOrWhiteSpace(row.LocalizedTitle) &&
            !string.IsNullOrWhiteSpace(row.LocalizedContent))
        {
            row.LastLocalizedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
        }

        return true;
    }

    private async Task TranslateAndSaveAsync(string source, Action<string> setter, string culture)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        try
        {
            var translated = await documentTranslationService.TranslateAsync(source, culture);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                setter(translated);
                await dbContext.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LocalizeDocumentsJob: translation failed, will retry next run.");
        }
    }
}
