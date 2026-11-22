using System.Globalization;
using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Services.FileStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

public class IndexDocumentsJob(
    DocsViewerDbContext db,
    StorageRootPathProvider storageRootPathProvider,
    NavConfigParser navConfigParser,
    FeatureFoldersProvider featureFolders,
    IMemoryCache cache,
    ILogger<IndexDocumentsJob> logger) : IBackgroundJob
{
    public string Name => "Index Documents";
    public string Description => "Index documents from the cloned repository.";

    public async Task ExecuteAsync()
    {
        logger.LogInformation("IndexDocumentsJob started.");

        var repoPath = Path.Combine(storageRootPathProvider.GetStorageRootPath(), "repo");
        if (!Directory.Exists(repoPath))
        {
            logger.LogWarning("DocsRepo directory not found at {RepoPath}", repoPath);
            return;
        }

        // Cleanup legacy documents with backslashes
        var legacyDocs = await db.Documents.IgnoreQueryFilters().Where(d => d.FilePath.Contains("\\")).ToListAsync();
        if (legacyDocs.Count > 0)
        {
            foreach (var legacyDoc in legacyDocs)
            {
                var normalizedPath = legacyDoc.FilePath.Replace('\\', '/');
                var bugDocs = await db.Documents.IgnoreQueryFilters()
                    .Where(d => d.FilePath == normalizedPath && d.Id != legacyDoc.Id)
                    .ToListAsync();
                
                db.Documents.RemoveRange(bugDocs);
                legacyDoc.FilePath = normalizedPath;
                legacyDoc.IsDeleted = false; // Restore it
            }
            await db.SaveChangesAsync();
            logger.LogInformation("Normalized {Count} legacy document paths and removed duplicates.", legacyDocs.Count);
        }

        var navConfig = await navConfigParser.ParseAsync(repoPath);
        var navTitleMap = navConfig != null
            ? BuildNavTitleMap(navConfig.NavItems)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var docsDir = navConfig?.DocsDir ?? "Docs";
        var baseDocPath = Path.Combine(repoPath, docsDir);

        if (!Directory.Exists(baseDocPath))
        {
            logger.LogWarning("Base doc path not found at {BaseDocPath}", baseDocPath);
            return;
        }

        var allMdFiles = Directory.GetFiles(baseDocPath, "*.md", SearchOption.AllDirectories);
        var foundPaths = new HashSet<string>();

        foreach (var file in allMdFiles)
        {
            var relativeFilePath = Path.GetRelativePath(baseDocPath, file).Replace('\\', '/');
            foundPaths.Add(relativeFilePath);

            var parts = relativeFilePath.Split('/');
            var category = parts.Length > 1 ? parts[0] : "root";
            var title = navTitleMap.GetValueOrDefault(relativeFilePath, Path.GetFileNameWithoutExtension(file));
            var rawContent = await File.ReadAllTextAsync(file);

            // Copy referenced images to StorageService Workspace and rewrite paths
            var docDir = Path.GetDirectoryName(file)!;
            var processedContent = DocumentImageStorage.PrepareImages(repoPath, docDir, rawContent, featureFolders, logger);

            var lastModified = await GetGitLastModifiedAsync(repoPath, file);

            var existingDoc = await db.Documents.FirstOrDefaultAsync(d => d.FilePath == relativeFilePath);

            if (existingDoc == null)
            {
                var newDoc = new Document
                {
                    Title = title,
                    Category = category,
                    FilePath = relativeFilePath,
                    Content = processedContent,
                    FileLastModified = lastModified,
                    IsDeleted = false
                };
                db.Documents.Add(newDoc);
                logger.LogInformation("Added new document: {FilePath}", relativeFilePath);
            }
            else
            {
                existingDoc.IsDeleted = false;

                if (existingDoc.Title != title || existingDoc.Content != processedContent || existingDoc.FileLastModified != lastModified)
                {
                    if (existingDoc.Title != title || existingDoc.Content != processedContent)
                    {
                        var translations = await db.LocalizedDocuments
                            .Where(localized => localized.DocumentId == existingDoc.Id)
                            .ToListAsync();

                        var imagePathChanges = existingDoc.Title == title
                            ? DocumentImageStorage.GetImagePathChanges(existingDoc.Content, processedContent)
                            : null;
                        if (imagePathChanges != null)
                        {
                            // Keep the translated text and its timestamp when only stored image URLs change.
                            foreach (var translation in translations)
                            {
                                translation.LocalizedContent = DocumentImageStorage.ReplaceImagePaths(
                                    translation.LocalizedContent, imagePathChanges);
                            }
                        }
                        else
                        {
                            db.LocalizedDocuments.RemoveRange(translations);
                            existingDoc.SourceCulture = null;
                        }
                    }

                    existingDoc.Title = title;
                    existingDoc.Content = processedContent;
                    existingDoc.FileLastModified = lastModified;
                    logger.LogInformation("Updated document: {FilePath}", relativeFilePath);
                }
            }
        }

        var allDocs = await db.Documents.Where(d => !d.IsDeleted).ToListAsync();
        foreach (var doc in allDocs)
        {
            if (!foundPaths.Contains(doc.FilePath))
            {
                doc.IsDeleted = true;
                logger.LogInformation("Soft deleted document: {FilePath}", doc.FilePath);
            }
        }

        await db.SaveChangesAsync();
        cache.Remove("document_tree");
        logger.LogInformation("IndexDocumentsJob completed.");
    }

    /// <summary>
    /// Flattens the properdocs.yml nav tree into a filePath → displayName map
    /// so IndexDocumentsJob can use the human-readable title instead of the filename.
    /// </summary>
    private static Dictionary<string, string> BuildNavTitleMap(List<NavEntry> entries)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        WalkNavEntries(entries, map);
        return map;
    }

    private static void WalkNavEntries(List<NavEntry> entries, Dictionary<string, string> map)
    {
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.Path))
            {
                var normalizedPath = entry.Path.TrimStart('.', '/', '\\').Replace('\\', '/');
                map[normalizedPath] = entry.Title;
            }
            if (entry.Children.Count > 0)
            {
                WalkNavEntries(entry.Children, map);
            }
        }
    }

    private async Task<DateTime> GetGitLastModifiedAsync(string repoPath, string absoluteFilePath)
    {
        var relativeToRepo = Path.GetRelativePath(repoPath, absoluteFilePath).Replace('\\', '/');
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"log -1 --format=%cI -- \"{relativeToRepo}\"",
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (DateTimeOffset.TryParse(output.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.UtcDateTime;
        }

        logger.LogWarning(
            "IndexDocumentsJob: could not parse git log date for '{File}'. Using UtcNow.",
            absoluteFilePath);
        return DateTime.UtcNow;
    }
}
