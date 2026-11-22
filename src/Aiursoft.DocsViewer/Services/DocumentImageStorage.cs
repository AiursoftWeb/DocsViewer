using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Aiursoft.DocsViewer.Services.FileStorage;

namespace Aiursoft.DocsViewer.Services;

public static partial class DocumentImageStorage
{
    [GeneratedRegex(@"(?<prefix>!\[[^\]]*\]\()(?<path>[^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex ImageRefRegex();

    // Existing databases can contain the older 16-digit identifiers.
    [GeneratedRegex(@"(?<prefix>!\[[^\]]*\]\()(?<path>doc-images/(?:[0-9a-f]{16}|[0-9a-f]{64})\.[^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex StoredImageRefRegex();

    public static string PrepareImages(
        string repoPath, string documentDirectory, string markdown, FeatureFoldersProvider folders, ILogger logger)
    {
        return ImageRefRegex().Replace(markdown, match =>
        {
            var originalPath = match.Groups["path"].Value;
            if (originalPath.StartsWith("//", StringComparison.Ordinal) ||
                originalPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                originalPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                originalPath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            var cleanPath = originalPath.Replace('\\', '/').TrimStart('/');
            var sourcePath = Path.GetFullPath(Path.Combine(documentDirectory, cleanPath));
            var relativePath = Path.GetRelativePath(repoPath, sourcePath);
            if (relativePath == ".." || Path.IsPathRooted(relativePath) ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                !File.Exists(sourcePath) || !IsRepositoryFile(repoPath, relativePath))
            {
                logger.LogWarning("Could not read document image at '{ImagePath}'. Keeping original URL.", originalPath);
                return match.Value;
            }

            var extension = Path.GetExtension(cleanPath).ToLowerInvariant();
            if (extension.Length == 0)
            {
                extension = ".png";
            }

            using var source = File.OpenRead(sourcePath);
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(source));
            var logicalPath = $"doc-images/{fingerprint}{extension}";
            var destination = Path.Combine(folders.GetWorkspaceFolder(), logicalPath);
            if (!File.Exists(destination))
            {
                var directory = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(directory);
                var temporaryPath = Path.Combine(directory, Path.GetRandomFileName());
                try
                {
                    source.Position = 0;
                    using (var target = File.Create(temporaryPath))
                    {
                        source.CopyTo(target);
                    }
                    File.Move(temporaryPath, destination, overwrite: true);
                }
                finally
                {
                    File.Delete(temporaryPath);
                }
            }

            return $"{match.Groups["prefix"].Value}{logicalPath})";
        });
    }

    // Git repositories can contain symbolic links. Only publish regular files inside the checkout.
    private static bool IsRepositoryFile(string repoPath, string relativePath)
    {
        var path = repoPath;
        foreach (var component in relativePath.Split(Path.DirectorySeparatorChar))
        {
            path = Path.Combine(path, component);
            if (component.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
        }

        return true;
    }

    public static Dictionary<string, string>? GetImagePathChanges(string previousMarkdown, string currentMarkdown)
    {
        var previousImages = StoredImageRefRegex().Matches(previousMarkdown);
        var currentImages = StoredImageRefRegex().Matches(currentMarkdown);
        if (previousImages.Count == 0 || previousImages.Count != currentImages.Count)
        {
            return null;
        }

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < previousImages.Count; i++)
        {
            var previous = previousImages[i].Groups["path"].Value;
            var current = currentImages[i].Groups["path"].Value;
            if (!paths.TryAdd(previous, current) && paths[previous] != current)
            {
                return null;
            }
        }

        // Verify that every character outside the generated image URLs is unchanged.
        return ReplaceImagePaths(previousMarkdown, paths) == currentMarkdown ? paths : null;
    }

    public static string ReplaceImagePaths(string markdown, IReadOnlyDictionary<string, string> paths) =>
        StoredImageRefRegex().Replace(markdown, match => paths.TryGetValue(match.Groups["path"].Value, out var path)
            ? $"{match.Groups["prefix"].Value}{path})"
            : match.Value);
}
