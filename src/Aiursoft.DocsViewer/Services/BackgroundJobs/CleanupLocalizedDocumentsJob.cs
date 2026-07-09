using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DocsViewer.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

public class CleanupLocalizedDocumentsJob(
    DocsViewerDbContext db,
    ILogger<CleanupLocalizedDocumentsJob> logger) : IBackgroundJob
{
    public string Name => "Cleanup Localized Documents";

    public string Description => "Deletes localized documents associated with soft-deleted documents.";

    public async Task ExecuteAsync()
    {
        logger.LogInformation("CleanupLocalizedDocumentsJob started.");

        var orphanLocalizedDocs = await db.LocalizedDocuments
            .IgnoreQueryFilters()
            .Where(ld => ld.Document.IsDeleted)
            .ToListAsync();

        if (orphanLocalizedDocs.Any())
        {
            db.LocalizedDocuments.RemoveRange(orphanLocalizedDocs);
            await db.SaveChangesAsync();
            logger.LogInformation("CleanupLocalizedDocumentsJob completed. Deleted {Count} orphan localized documents.", orphanLocalizedDocs.Count);
        }
        else
        {
            logger.LogInformation("CleanupLocalizedDocumentsJob completed. No orphan localized documents found.");
        }
    }
}
