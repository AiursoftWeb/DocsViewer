using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Services.FileStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

public partial class IndexDocumentsJob(
    DocsViewerDbContext db,
    IHostEnvironment env,
    NavConfigParser navConfigParser,
    FeatureFoldersProvider featureFolders,
    IMemoryCache cache,
    ILogger<IndexDocumentsJob> logger) : IBackgroundJob
{
    public string Name => "Index Documents";
    public string Description => "Index documents from the cloned repository.";

    /// <summary>
    /// Matches markdown image syntax: ![alt text](relative/path.png)
    /// Captures the path part (group 1).
    /// </summary>
    [GeneratedRegex(@"!\[[^\]]*\]\(([^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex ImageRefRegex();

    public async Task ExecuteAsync()
    {
        logger.LogInformation("IndexDocumentsJob started.");

        var repoPath = Path.Combine(env.ContentRootPath, "App_Data", "DocsRepo");
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
            var processedContent = ProcessImages(docDir, rawContent);

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
                    bool contentChanged = existingDoc.Title != title || existingDoc.Content != processedContent;
                    
                    existingDoc.Title = title;
                    existingDoc.Content = processedContent;
                    existingDoc.FileLastModified = lastModified;
                    
                    if (contentChanged)
                    {
                        existingDoc.SourceCulture = null; // trigger re-detection only if content changed
                    }
                    
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
    /// Finds all markdown image references, copies the files to StorageService Workspace,
    /// and returns the content with paths rewritten to logical paths.
    /// </summary>
    private string ProcessImages(string docDir, string markdown)
    {
        return ImageRefRegex().Replace(markdown, match =>
        {
            var originalPath = match.Groups[1].Value;

            // Skip external URLs
            if (originalPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                originalPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                originalPath.StartsWith("data:"))
            {
                return match.Value;
            }

            // Resolve relative path against the document directory
            var cleanPath = originalPath.TrimStart('.', '/', '\\');
            var sourcePath = Path.Combine(docDir, cleanPath);

            if (!File.Exists(sourcePath))
            {
                logger.LogWarning("IndexDocumentsJob: image not found at '{Source}'. Keeping original path.", sourcePath);
                return match.Value;
            }

            var ext = Path.GetExtension(cleanPath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = ".png";

            var uuid = ComputeImageFingerprint(cleanPath, sourcePath);
            var logicalPath = $"doc-images/{uuid}{ext}";
            var workspaceRoot = featureFolders.GetWorkspaceFolder();
            var physicalPath = Path.GetFullPath(Path.Combine(workspaceRoot, logicalPath));

            var dir = Path.GetDirectoryName(physicalPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.Copy(sourcePath, physicalPath, overwrite: true);

            // Rewrite to logical path — will be resolved at render time via StorageService
            return $"![{match.Groups[1].Value}]({logicalPath})";
        });
    }

    /// <summary>
    /// Computes a deterministic fingerprint for an image file based on its relative path,
    /// file size, and last-write time. Same file always produces the same fingerprint;
    /// any change to the file metadata produces a new one. Exposed as protected virtual
    /// so unit tests can verify determinism directly.
    /// </summary>
    protected virtual string ComputeImageFingerprint(string relativePath, string absolutePath)
    {
        var fileInfo = new FileInfo(absolutePath);
        var fingerprint = $"{relativePath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))
        )[..16].ToLowerInvariant();
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

        if (DateTimeOffset.TryParse(output.Trim(), out var dt))
        {
            return dt.UtcDateTime;
        }

        logger.LogWarning(
            "IndexDocumentsJob: could not parse git log date for '{File}'. Using UtcNow.",
            absoluteFilePath);
        return DateTime.UtcNow;
    }
}
