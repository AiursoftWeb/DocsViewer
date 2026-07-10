using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

/// <summary>
/// Removes LocalizedDocument rows that are no longer meaningful:
/// (1) rows whose parent Document has been soft-deleted,
/// (2) rows for cultures that are no longer in the configured LocalizationLanguages setting.
/// Without this, the table grows unboundedly because documents change often and
/// cultures may be removed over time.
///
/// A staleness guard (LastLocalizedAt age >= <see cref="StalenessThreshold"/>)
/// prevents a delete-then-localize ping-pong when <see cref="LocalizeDocumentsJob"/>
/// is still running with an older view of the configured languages and would
/// otherwise re-create rows that this job just removed.
/// </summary>
public class CleanupLocalizedDocumentsJob(
    DocsViewerDbContext db,
    GlobalSettingsService settingsService,
    ILogger<CleanupLocalizedDocumentsJob> logger) : IBackgroundJob
{
    /// <summary>
    /// Only rows whose <see cref="LocalizedDocument.LastLocalizedAt"/> is older than
    /// this threshold are eligible for cleanup.  Rows created/updated more recently
    /// are left alone so that a concurrently-running <see cref="LocalizeDocumentsJob"/>
    /// (which may still hold a stale view of the configured languages) can finish
    /// its current batch without being undone.
    /// </summary>
    internal static readonly TimeSpan StalenessThreshold = TimeSpan.FromMinutes(10);

    public string Name => "Cleanup Localized Documents";

    public string Description =>
        "Removes LocalizedDocument rows that are orphaned (parent Document soft-deleted) " +
        "or belong to cultures no longer in the configured localization languages list.";

    public async Task ExecuteAsync()
    {
        logger.LogInformation("CleanupLocalizedDocumentsJob started.");

        var languagesRaw = await settingsService.GetSettingValueAsync(SettingsMap.LocalizationLanguages);
        var configuredCultures = languagesRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();

        var staleCutoff = DateTime.UtcNow - StalenessThreshold;
        var totalDeleted = 0;

        // 1. Delete LocalizedDocuments whose parent Document is soft-deleted.
        // Materialise the list of deleted Document IDs first to avoid MySQL error 1093:
        // "You can't specify target table 'l' for update in FROM clause".
        var deletedDocIds = await db.Documents
            .IgnoreQueryFilters()
            .Where(d => d.IsDeleted)
            .Select(d => d.Id)
            .ToListAsync();

        if (deletedDocIds.Count > 0)
        {
            var orphaned = await db.LocalizedDocuments
                .IgnoreQueryFilters()
                .Where(ld => deletedDocIds.Contains(ld.DocumentId) && ld.LastLocalizedAt < staleCutoff)
                .ExecuteDeleteAsync();

            if (orphaned > 0)
            {
                totalDeleted += orphaned;
                logger.LogInformation(
                    "CleanupLocalizedDocumentsJob: deleted {Count} orphaned row(s) (parent Document soft-deleted).",
                    orphaned);
            }
        }

        // 2. Delete LocalizedDocuments for cultures no longer in the configured languages list.
        if (configuredCultures.Count > 0)
        {
            var staleCulture = await db.LocalizedDocuments
                .IgnoreQueryFilters()
                .Where(ld => !configuredCultures.Contains(ld.Culture) && ld.LastLocalizedAt < staleCutoff)
                .ExecuteDeleteAsync();

            if (staleCulture > 0)
            {
                totalDeleted += staleCulture;
                logger.LogInformation(
                    "CleanupLocalizedDocumentsJob: deleted {Count} row(s) for removed cultures.",
                    staleCulture);
            }
        }

        logger.LogInformation(
            "CleanupLocalizedDocumentsJob finished. {Total} row(s) deleted.",
            totalDeleted);
    }
}
