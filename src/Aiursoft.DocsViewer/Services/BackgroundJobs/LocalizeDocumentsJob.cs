using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

public class LocalizeDocumentsJob(
    DocsViewerDbContext dbContext,
    GlobalSettingsService globalSettingsService,
    IDocumentTranslationService documentTranslationService,
    ILogger<LocalizeDocumentsJob> logger) : IBackgroundJob
{
    public string Name => "Localize Documents Job";
    public string Description => "Translates documents to supported cultures.";

    public async Task ExecuteAsync()
    {
        if (!await globalSettingsService.IsAiLocalizationEnabledAsync())
        {
            return;
        }

        var culturesString = await globalSettingsService.GetSettingValueAsync(SettingsMap.LocalizationLanguages);
        if (string.IsNullOrWhiteSpace(culturesString))
        {
            return;
        }

        var cultures = culturesString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var documents = await dbContext.Documents
            .Include(d => d.LocalizedDocuments)
            .ToListAsync();

        foreach (var document in documents)
        {
            foreach (var culture in cultures)
            {
                var localized = document.LocalizedDocuments.FirstOrDefault(l => l.Culture == culture);

                if (localized == null || 
                    string.IsNullOrWhiteSpace(localized.LocalizedContent) || 
                    localized.LastLocalizedAt < document.FileLastModified)
                {
                    logger.LogInformation("Translating document {Id} to {Culture}", document.Id, culture);
                    var translatedTitle = await documentTranslationService.TranslateAsync(document.Title, culture);
                    var translatedContent = await documentTranslationService.TranslateAsync(document.Content, culture);

                    if (localized == null)
                    {
                        localized = new LocalizedDocument
                        {
                            DocumentId = document.Id,
                            Culture = culture,
                            LocalizedTitle = translatedTitle,
                            LocalizedContent = translatedContent,
                            LastLocalizedAt = DateTime.UtcNow
                        };
                        dbContext.LocalizedDocuments.Add(localized);
                    }
                    else
                    {
                        localized.LocalizedTitle = translatedTitle;
                        localized.LocalizedContent = translatedContent;
                        localized.LastLocalizedAt = DateTime.UtcNow;
                    }
                }
            }
        }

        await dbContext.SaveChangesAsync();
    }
}
