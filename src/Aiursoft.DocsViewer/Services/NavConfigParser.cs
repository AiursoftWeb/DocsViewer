using Aiursoft.Scanner.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aiursoft.DocsViewer.Services;

/// <summary>
/// Parses an MkDocs properdocs.yml file to extract:
/// - docs_dir: where .md files live (e.g. "Docs")
/// - nav: ordered sidebar tree with display names and file paths
/// - homePage: the first document in the nav tree (for the / route)
/// </summary>
public class NavConfigParser(ILogger<NavConfigParser> logger) : IScopedDependency
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<NavConfig?> ParseAsync(string repoPath)
    {
        var ymlPath = Path.Combine(repoPath, "properdocs.yml");
        if (!File.Exists(ymlPath))
        {
            logger.LogInformation("NavConfigParser: properdocs.yml not found at {Path}. Using alphabetical order.", ymlPath);
            return null;
        }

        try
        {
            var yaml = await File.ReadAllTextAsync(ymlPath);
            var config = Deserializer.Deserialize<MkDocsConfig>(yaml);

            if (config.Nav == null || config.Nav.Count == 0)
            {
                logger.LogWarning("NavConfigParser: properdocs.yml has no nav entries. Using alphabetical order.");
                return null;
            }

            var navItems = ParseNavList(config.Nav);
            var docsDir = string.IsNullOrWhiteSpace(config.DocsDir) ? "." : config.DocsDir.Trim('/');
            var homePage = FindFirstDocument(navItems);

            return new NavConfig
            {
                DocsDir = docsDir,
                NavItems = navItems,
                HomePage = homePage
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NavConfigParser: failed to parse properdocs.yml. Using alphabetical order.");
            return null;
        }
    }

    private static List<NavEntry> ParseNavList(List<object> entries)
    {
        var result = new List<NavEntry>();
        foreach (var entry in entries)
        {
            if (entry is Dictionary<object, object> dict)
            {
                foreach (var kv in dict)
                {
                    var key = kv.Key.ToString()!;
                    if (kv.Value is string strValue)
                    {
                        // Leaf: "DisplayName: path/to/file.md" or external url
                        result.Add(new NavEntry { Title = key, Path = strValue });
                    }
                    else if (kv.Value is List<object> children)
                    {
                        // Branch: "GroupName:" with sub-items
                        result.Add(new NavEntry
                        {
                            Title = key,
                            Children = ParseNavList(children)
                        });
                    }
                }
            }
        }
        return result;
    }

    private static string? FindFirstDocument(List<NavEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.Path) && !IsExternalUrl(entry.Path))
                return entry.Path;
            if (entry.Children.Count > 0)
            {
                var found = FindFirstDocument(entry.Children);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static bool IsExternalUrl(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

public class NavConfig
{
    public string DocsDir { get; set; } = ".";
    public List<NavEntry> NavItems { get; set; } = [];
    public string? HomePage { get; set; }
}

public class NavEntry
{
    public string Title { get; set; } = "";
    public string? Path { get; set; }
    public List<NavEntry> Children { get; set; } = [];
}

// Minimal DTOs for deserializing only the fields we need
public class MkDocsConfig
{
    [YamlMember(Alias = "docs_dir")]
    public string? DocsDir { get; set; }

    [YamlMember(Alias = "nav")]
    public List<object>? Nav { get; set; }
}
