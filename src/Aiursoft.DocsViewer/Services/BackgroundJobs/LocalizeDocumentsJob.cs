using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

/// <summary>
/// Translates documents into configured target languages.
///
/// SourceCulture routing:
/// - SourceCulture is null            → Skip (pending detection by DetectSourceCultureJob)
/// - targetCulture == SourceCulture   → Pass-through (copy original content, no AI call)
/// - targetCulture != SourceCulture   → Translate (call AI to translate)
///
/// Staleness is tracked per (document × culture) pair against
/// <see cref="Document.FileLastModified"/>.
/// </summary>
public class LocalizeDocumentsJob(
    DocsViewerDbContext dbContext,
    GlobalSettingsService globalSettingsService,
    IDocumentTranslationService documentTranslationService,
    ILogger<LocalizeDocumentsJob> logger) : IBackgroundJob
{
    public string Name => "Localize Documents";

    public string Description =>
        "Translates documents into all configured languages. " +
        "Documents whose SourceCulture is null are skipped (pending language detection). " +
        "When the target language matches the document's SourceCulture, " +
        "the original content is copied through without calling AI. " +
        "When they differ, the content is translated via the configured AI endpoint. " +
        "Staleness is tracked per (document × culture) pair against Document.FileLastModified.";

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
                    .Where(d => d.SourceCulture != null &&
                                !d.IsDeleted &&
                                d.Id > currentLastId &&
                                !dbContext.LocalizedDocuments.Any(ld =>
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
                        await dbContext.SaveChangesAsync();
                    }
                }

                lastId = pendingDocs.Max(d => d.Id);
                logger.LogInformation(
                    "LocalizeDocumentsJob: [{Culture}] batch finished. Last ID: {LastId}. Total so far: {Total}.",
                    culture, lastId, totalProcessed);
            }

            logger.LogInformation("LocalizeDocumentsJob: [{Culture}] all documents up-to-date.", culture);
        }

        logger.LogInformation("LocalizeDocumentsJob: done. Processed {Count} pair(s) this run.", totalProcessed);
    }

    private async Task<bool> LocalizeDocumentAsync(Document document, string culture)
    {
        try
        {
            // Pass-through: same language — copy original content, no AI call.
            if (string.Equals(document.SourceCulture, culture, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "LocalizeDocumentsJob: pass-through '{Title}' (source={SourceCulture}, target={TargetCulture}).",
                    document.Title, document.SourceCulture, culture);

                await SaveLocalizedAsync(document, culture,
                    document.Title, document.Content);
                return true;
            }

            // Translate: different language — call AI.
            logger.LogInformation(
                "LocalizeDocumentsJob: translating '{Title}' (source={SourceCulture} → {TargetCulture}).",
                document.Title, document.SourceCulture, culture);

            var titleTask   = documentTranslationService.TranslateAsync(document.Title, culture);
            var contentTask = documentTranslationService.TranslateAsync(document.Content, culture);
            await Task.WhenAll(titleTask, contentTask);

            await SaveLocalizedAsync(document, culture, await titleTask, await contentTask);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "LocalizeDocumentsJob: failed to localize '{Title}' to {Culture}.",
                document.Title, culture);
            return false;
        }
    }

    private async Task SaveLocalizedAsync(Document doc, string culture,
        string title, string content)
    {
        var existing = await dbContext.LocalizedDocuments
            .FirstOrDefaultAsync(ld => ld.DocumentId == doc.Id && ld.Culture == culture);

        if (existing == null)
        {
            dbContext.LocalizedDocuments.Add(new LocalizedDocument
            {
                DocumentId       = doc.Id,
                Culture          = culture,
                LocalizedTitle   = title,
                LocalizedContent = content,
                LastLocalizedAt  = DateTime.UtcNow
            });
        }
        else
        {
            existing.LocalizedTitle   = title;
            existing.LocalizedContent = content;
            existing.LastLocalizedAt  = DateTime.UtcNow;
        }
    }
}
