using Aiursoft.DocsViewer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.DocsViewer.Services;

public class DocumentTreeNode
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public Document? Document { get; set; }
    public List<DocumentTreeNode> Children { get; set; } = [];
}

public class DocumentTreeService(
    DocsViewerDbContext db,
    IMemoryCache cache,
    IHostEnvironment env,
    NavConfigParser navConfigParser) : IScopedDependency
{
    public async Task<List<DocumentTreeNode>> GetTreeAsync(CancellationToken ct = default)
    {
        return await cache.GetOrCreateAsync("document_tree", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            var docs = await db.Documents
                .AsNoTracking()
                .ToListAsync(ct);

            var docLookup = docs.ToDictionary(
                d => d.FilePath.Replace('\\', '/'),
                StringComparer.OrdinalIgnoreCase);

            // Try to use properdocs.yml ordering
            var repoPath = Path.Combine(env.ContentRootPath, "App_Data", "DocsRepo");
            var navConfig = await navConfigParser.ParseAsync(repoPath);
            if (navConfig != null)
            {
                return BuildOrderedTree(navConfig.NavItems, docLookup);
            }

            // Fallback: alphabetical
            return BuildAlphaTree(docLookup);
        }) ?? [];
    }

    private static List<DocumentTreeNode> BuildOrderedTree(
        List<NavEntry> entries, Dictionary<string, Document> docLookup)
    {
        var result = new List<DocumentTreeNode>();
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.Path))
            {
                if (docLookup.TryGetValue(entry.Path, out var doc))
                {
                    result.Add(new DocumentTreeNode
                    {
                        Name = entry.Title,
                        Path = entry.Path,
                        Document = doc
                    });
                }
            }
            else
            {
                var children = BuildOrderedTree(entry.Children, docLookup);
                if (children.Count > 0)
                {
                    result.Add(new DocumentTreeNode
                    {
                        Name = entry.Title,
                        Path = entry.Title,
                        Children = children
                    });
                }
            }
        }
        return result;
    }

    private static List<DocumentTreeNode> BuildAlphaTree(
        Dictionary<string, Document> docLookup)
    {
        var rootNodes = new List<DocumentTreeNode>();
        foreach (var kv in docLookup.OrderBy(d => d.Key))
        {
            var segments = kv.Key.Split('/');
            if (segments.Length == 0) continue;
            var currentLevel = rootNodes;
            for (var i = 0; i < segments.Length; i++)
            {
                var seg = segments[i];
                var isLast = i == segments.Length - 1;
                var node = currentLevel.FirstOrDefault(n => n.Name == seg);
                if (node == null)
                {
                    node = new DocumentTreeNode
                    {
                        Name = seg,
                        Path = string.Join("/", segments.Take(i + 1)),
                        Document = isLast ? kv.Value : null
                    };
                    currentLevel.Add(node);
                }
                currentLevel = node.Children;
            }
        }
        return rootNodes;
    }
}
