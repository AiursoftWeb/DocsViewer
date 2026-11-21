using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aiursoft.DocsViewer.Services.FileStorage;

namespace Aiursoft.DocsViewer.Services;

public static partial class DocumentImageStorage
{
    [GeneratedRegex(@"(?<prefix>!\[[^\]]*\]\()(?<path>[^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex ImageRefRegex();

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

            var logicalPath = $"doc-images/{ComputeFingerprint(cleanPath, sourcePath)}{extension}";
            var destination = Path.Combine(folders.GetWorkspaceFolder(), logicalPath);
            if (!File.Exists(destination))
            {
                var directory = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(directory);
                var temporaryPath = Path.Combine(directory, Path.GetRandomFileName());
                try
                {
                    File.Copy(sourcePath, temporaryPath);
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

    public static string ComputeFingerprint(string relativePath, string absolutePath)
    {
        var fileInfo = new FileInfo(absolutePath);
        var fingerprint = $"{relativePath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..16].ToLowerInvariant();
    }
}
