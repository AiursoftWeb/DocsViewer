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

public class DocumentTreeService(DocsViewerDbContext db, IMemoryCache cache) : IScopedDependency
{
    public async Task<List<DocumentTreeNode>> GetTreeAsync(CancellationToken ct = default)
    {
        return await cache.GetOrCreateAsync("document_tree", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            
            var docs = await db.Documents
                .AsNoTracking()
                .OrderBy(d => d.FilePath)
                .ToListAsync(ct);

            var rootNodes = new List<DocumentTreeNode>();

            foreach (var doc in docs)
            {
                // FilePath: "Folder1/Folder2/Folder3/Doc.md"
                // Split by '/' or '\'
                var segments = doc.FilePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0) continue;

                var currentLevel = rootNodes;
                string currentPath = "";

                // Build tree up to 4 levels
                for (int i = 0; i < Math.Min(segments.Length, 4); i++)
                {
                    var segment = segments[i];
                    var isLastSegment = i == segments.Length - 1 || i == 3;
                    
                    if (isLastSegment && i == 3 && segments.Length > 4)
                    {
                        // If it exceeds 4 levels, flatten the remaining into the 4th level name
                        segment = string.Join('/', segments.Skip(3));
                    }
                    
                    currentPath = string.IsNullOrEmpty(currentPath) ? segment : $"{currentPath}/{segment}";

                    var node = currentLevel.FirstOrDefault(n => n.Name == segment);
                    if (node == null)
                    {
                        node = new DocumentTreeNode
                        {
                            Name = segment,
                            Path = currentPath,
                            Document = isLastSegment ? doc : null
                        };
                        currentLevel.Add(node);
                    }
                    
                    currentLevel = node.Children;
                }
            }

            void SortTree(List<DocumentTreeNode> nodes)
            {
                nodes.Sort((a, b) => 
                {
                    var aIsFile = a.Document != null;
                    var bIsFile = b.Document != null;
                    if (aIsFile && !bIsFile) return -1;
                    if (!aIsFile && bIsFile) return 1;
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                foreach (var node in nodes)
                {
                    if (node.Children.Count > 0)
                    {
                        SortTree(node.Children);
                    }
                }
            }
            SortTree(rootNodes);

            return rootNodes;
        }) ?? [];
    }
}
