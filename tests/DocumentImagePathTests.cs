using Aiursoft.DocsViewer.InMemory;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Services.BackgroundJobs;
using Aiursoft.DocsViewer.Services.FileStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public class DocumentImagePathTests
{
    [TestMethod]
    [DataRow("../Applications/System/Driver-Center/images/driver-center-xbox.png")]
    [DataRow("./images/screenshot.png")]
    [DataRow("images/screenshot.png")]
    public async Task IndexingCopiesImagesRelativeToTheirDocument(string imageReference)
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocumentImagePathTest_{Guid.NewGuid():N}");
        var docDir = Path.Combine(root, "repo", "Docs", "Install");
        Directory.CreateDirectory(docDir);
        try
        {
            var imagePath = Path.GetFullPath(Path.Combine(docDir, imageReference));
            Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
            var imageBytes = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aHioAAAAASUVORK5CYII=");
            await File.WriteAllBytesAsync(imagePath, imageBytes);
            await File.WriteAllTextAsync(Path.Combine(docDir, "Install-Drivers.md"),
                $"![Xbox]({imageReference})\n![Missing](missing.png)\n![External](https://example.com/image.png)");

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Storage:Path"] = root }).Build();
            var rootProvider = new StorageRootPathProvider(configuration);
            var folders = new FeatureFoldersProvider(rootProvider);
            await using var db = new InMemoryContext(new DbContextOptionsBuilder<InMemoryContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var job = new IndexDocumentsJob(db, rootProvider,
                new NavConfigParser(NullLogger<NavConfigParser>.Instance), folders, cache,
                NullLogger<IndexDocumentsJob>.Instance);

            await job.ExecuteAsync();

            var content = (await db.Documents.SingleAsync()).Content;
            StringAssert.Contains(content, "](doc-images/");
            StringAssert.Contains(content, "![Missing](missing.png)");
            StringAssert.Contains(content, "![External](https://example.com/image.png)");
            var copiedImage = Directory.GetFiles(Path.Combine(folders.GetWorkspaceFolder(), "doc-images")).Single();
            CollectionAssert.AreEqual(imageBytes, await File.ReadAllBytesAsync(copiedImage));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
