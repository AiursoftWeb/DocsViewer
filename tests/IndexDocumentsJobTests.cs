using Aiursoft.GitRunner;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.InMemory;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Services.BackgroundJobs;
using Aiursoft.DocsViewer.Services.FileStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public class IndexDocumentsJobTests
{
    private string _tempPath = null!;
    private string _mockRepoPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "IndexJobTest_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempPath);

        _mockRepoPath = Path.Combine(Path.GetTempPath(), "MockRepo_" + Guid.NewGuid());
        CreateMockGitRepo(_mockRepoPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempPath))
            Directory.Delete(_tempPath, recursive: true);
        if (Directory.Exists(_mockRepoPath))
            Directory.Delete(_mockRepoPath, recursive: true);
    }

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    private void CreateMockGitRepo(string path)
    {
        Directory.CreateDirectory(path);
        var docsPath = Path.Combine(path, "Docs", "test_category");
        Directory.CreateDirectory(docsPath);
        File.WriteAllText(Path.Combine(docsPath, "test_doc.md"), "# Test Doc\nThis is a test.");

        RunGitCommand("init --initial-branch=main", path);
        RunGitCommand("add .", path);
        // Use -c overrides so global GPG / hook settings don't break the test.
        RunGitCommand("-c user.name=TestUser -c user.email=test@test.com -c commit.gpgsign=false commit --no-gpg-sign -m \"Initial commit\"", path);
    }

    private static void RunGitCommand(string args, string path)
    {
        var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        p.Start();
        if (!p.WaitForExit(GitTimeout))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {args} timed out after {GitTimeout.TotalSeconds}s.");
        }
        if (p.ExitCode != 0)
        {
            throw new Exception($"git {args} failed (exit {p.ExitCode}): {p.StandardError.ReadToEnd()}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helper: builds all services from a shared config / db name
    // ─────────────────────────────────────────────────────────────────────────

    private (
        SyncDocsRepoJob syncJob,
        StorageRootPathProvider storageRootPathProvider,
        GlobalSettingsService globalSettings,
        ILoggerFactory loggerFactory,
        DbContextOptions<InMemoryContext> dbOptions,
        FeatureFoldersProvider foldersProvider,
        StorageService storageService,
        MemoryCache memoryCache
    ) BuildServices(string dbName)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Storage:Path", _tempPath },
                { $"GlobalSettings:{SettingsMap.DocsRepoUrl}", _mockRepoPath }
            })
            .Build();

        var memoryCache = new MemoryCache(new MemoryCacheOptions());

        var dbOptions = new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var sp = new ServiceCollection()
            .AddLogging()
            .AddGitRunner()
            .BuildServiceProvider();

        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        var rootProvider = new StorageRootPathProvider(config);
        var foldersProvider = new FeatureFoldersProvider(rootProvider);
        var fileLockProvider = new FileLockProvider(memoryCache);
        var storageService = new StorageService(foldersProvider, fileLockProvider, new EphemeralDataProtectionProvider());

        var globalSettings = new GlobalSettingsService(
            new InMemoryContext(dbOptions), config, storageService, memoryCache, new EphemeralDataProtectionProvider(), NullLogger<GlobalSettingsService>.Instance);

        var workspaceManager = sp.GetRequiredService<WorkspaceManager>();
        var syncJob = new SyncDocsRepoJob(
            globalSettings, workspaceManager, rootProvider, loggerFactory.CreateLogger<SyncDocsRepoJob>());

        return (syncJob, rootProvider, globalSettings, loggerFactory, dbOptions, foldersProvider, storageService, memoryCache);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("content")]
    [DataRow("title")]
    [DataRow("unchanged")]
    public async Task IndexingInvalidatesTranslationsOnlyWhenSourceChanges(string change)
    {
        var (syncJob, rootProvider, _, loggerFactory, options, folders, _, cache) =
            BuildServices("TranslationInvalidation_" + Guid.NewGuid());
        await syncJob.ExecuteAsync();
        await using var db = new InMemoryContext(options);
        var job = new IndexDocumentsJob(db, rootProvider,
            new NavConfigParser(NullLogger<NavConfigParser>.Instance), folders, cache,
            loggerFactory.CreateLogger<IndexDocumentsJob>());
        await job.ExecuteAsync();
        var document = await db.Documents.SingleAsync();
        var gitTimestamp = document.FileLastModified;
        document.SourceCulture = "en-US";
        // Simulate content saved by an older indexer, without touching the Git file.
        if (change == "content") document.Content = "![Snapshot](../images/snapshot.png)";
        if (change == "title") document.Title = "Old title";
        foreach (var culture in new[] { "zh-CN", "ja-JP" })
        {
            db.LocalizedDocuments.Add(new LocalizedDocument
            {
                DocumentId = document.Id,
                Culture = culture,
                LocalizedContent = "Old translation",
                LastLocalizedAt = gitTimestamp.AddDays(1)
            });
        }
        var unrelated = new Document
        {
            Title = "Other", Category = "root", FilePath = "other.md"
        };
        db.Documents.Add(unrelated);
        db.LocalizedDocuments.Add(new LocalizedDocument
        {
            Document = unrelated, Culture = "zh-CN", LocalizedContent = "Keep me"
        });
        await db.SaveChangesAsync();

        await job.ExecuteAsync();
        db.ChangeTracker.Clear();
        var indexed = await db.Documents.SingleAsync(d => d.Id == document.Id);
        Assert.AreEqual(gitTimestamp, indexed.FileLastModified);
        Assert.AreEqual(change == "unchanged" ? 2 : 0,
            await db.LocalizedDocuments.CountAsync(l => l.DocumentId == document.Id));
        Assert.AreEqual(change == "unchanged" ? "en-US" : null, indexed.SourceCulture);
        Assert.AreEqual("Keep me", (await db.LocalizedDocuments.IgnoreQueryFilters()
            .SingleAsync(l => l.DocumentId == unrelated.Id)).LocalizedContent);
    }

    [TestMethod]
    public async Task IndexDocumentsJob_SecondRun_WritesNothing()
    {
        var dbName = "IndexJobTest_" + Guid.NewGuid();
        var (syncJob, storageRootPathProvider, _, loggerFactory, dbOptions, foldersProvider, _, memCache) = BuildServices(dbName);

        // ── Step 1: sync git repo ────────────────────────────────────────────
        await syncJob.ExecuteAsync();

        var repoPath = Path.Combine(_tempPath, "repo");
        Assert.IsTrue(Directory.Exists(repoPath), "Repo must exist before indexing");

        // ── Step 2: first index run — should insert documents ──────────────────
        await using (var db = new InMemoryContext(dbOptions))
        {
            var job = new IndexDocumentsJob(
                db, storageRootPathProvider, new NavConfigParser(NullLogger<NavConfigParser>.Instance), foldersProvider, memCache,
                loggerFactory.CreateLogger<IndexDocumentsJob>());
            await job.ExecuteAsync();
        }

        int documentCount;
        await using (var db = new InMemoryContext(dbOptions))
        {
            documentCount = await db.Documents.CountAsync();
            var document = await db.Documents.FirstAsync();
            Assert.AreEqual("test_doc", document.Title,
                "First run must parse and store title from markdown.");
        }
        Assert.IsTrue(documentCount > 0,
            "First run must insert at least one document into the database.");

        // ── Step 3: second index run — must write NOTHING ────────────────────
        await using var strictDb = new NoWriteDbContext(dbOptions);
        var job2 = new IndexDocumentsJob(
            strictDb, storageRootPathProvider, new NavConfigParser(NullLogger<NavConfigParser>.Instance), foldersProvider, memCache,
            loggerFactory.CreateLogger<IndexDocumentsJob>());

        // This must not throw: NoWriteDbContext throws if SaveChanges has
        // any pending Added / Modified / Deleted entries.
        await job2.ExecuteAsync();

        // Sanity: record count must be identical after second run
        await using (var db = new InMemoryContext(dbOptions))
        {
            var countAfter = await db.Documents.CountAsync();
            Assert.AreEqual(documentCount, countAfter,
                "Second run must not add, update, or remove any documents.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test: documents indexed before calorie feature was added must be re-indexed
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IndexDocumentsJob_ReindexesWhenTitleChanged()
    {
        var dbName = "IndexJobTest_" + Guid.NewGuid();
        var (syncJob, storageRootPathProvider, _, loggerFactory, dbOptions, foldersProvider, _, memCache) = BuildServices(dbName);

        // ── Step 1: sync and index normally ────────────────────────────────────
        await syncJob.ExecuteAsync();

        await using (var db = new InMemoryContext(dbOptions))
        {
            var job = new IndexDocumentsJob(
                db, storageRootPathProvider, new NavConfigParser(NullLogger<NavConfigParser>.Instance), foldersProvider, memCache,
                loggerFactory.CreateLogger<IndexDocumentsJob>());
            await job.ExecuteAsync();
        }

        // ── Step 2: simulate pre-feature state by changing Title ────
        await using (var db = new InMemoryContext(dbOptions))
        {
            var document = await db.Documents.FirstAsync();
            Assert.AreEqual("test_doc", document.Title,
                "First run must parse and store title value.");
            Assert.IsNull(document.SourceCulture,
                "New documents must have null SourceCulture (awaiting detection).");

            document.Title = "Old Title";
            await db.SaveChangesAsync();
        }

        // ── Step 3: re-run index — must restore the title value ─────────────
        await using (var db = new InMemoryContext(dbOptions))
        {
            var job = new IndexDocumentsJob(
                db, storageRootPathProvider, new NavConfigParser(NullLogger<NavConfigParser>.Instance), foldersProvider, memCache,
                loggerFactory.CreateLogger<IndexDocumentsJob>());
            await job.ExecuteAsync();
        }

        await using (var db = new InMemoryContext(dbOptions))
        {
            var document = await db.Documents.FirstAsync();
            Assert.AreEqual("test_doc", document.Title,
                "Second run must re-index and restore title value for documents.");
            Assert.IsNull(document.SourceCulture,
                "SourceCulture must be reset to null when title changes.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test: document title comes from properdocs.yml nav entry, not filename
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IndexDocumentsJob_UsesNavTitleFromProperdocsYml()
    {
        // Create properdocs.yml in mock repo with a custom nav title
        var ymlPath = Path.Combine(_mockRepoPath, "properdocs.yml");
        await File.WriteAllTextAsync(ymlPath, """
            docs_dir: Docs
            nav:
              - Test:
                - "My Custom Title": test_category/test_doc.md
            """);
        // Commit so the file is part of the synced repo
        RunGitCommand("add properdocs.yml", _mockRepoPath);
        RunGitCommand("-c user.name=TestUser -c user.email=test@test.com -c commit.gpgsign=false commit --no-gpg-sign -m \"Add properdocs.yml\"", _mockRepoPath);

        var dbName = "IndexJobTest_" + Guid.NewGuid();
        var (syncJob, storageRootPathProvider, _, loggerFactory, dbOptions, foldersProvider, _, memCache) = BuildServices(dbName);

        // Sync and index
        await syncJob.ExecuteAsync();

        await using (var db = new InMemoryContext(dbOptions))
        {
            var job = new IndexDocumentsJob(
                db, storageRootPathProvider, new NavConfigParser(NullLogger<NavConfigParser>.Instance), foldersProvider, memCache,
                loggerFactory.CreateLogger<IndexDocumentsJob>());
            await job.ExecuteAsync();
        }

        await using (var db = new InMemoryContext(dbOptions))
        {
            var document = await db.Documents.FirstAsync();
            Assert.AreEqual("My Custom Title", document.Title,
                "Document title must come from properdocs.yml nav entry, not from filename.");
        }
    }

    [TestMethod]
    public async Task ReindexingPreservesTranslationsAndUpdatesImageUrls()
    {
        var (syncJob, rootProvider, _, loggerFactory, options, folders, _, cache) =
            BuildServices("ImageReindex_" + Guid.NewGuid());
        const string markdown = "# Test Doc\n![alt](logo.svg)\nThis is a test.";
        const string logo = "<svg>logo</svg>";
        var mockDocsDir = Path.Combine(_mockRepoPath, "Docs", "test_category");
        await File.WriteAllTextAsync(Path.Combine(mockDocsDir, "test_doc.md"), markdown);
        await File.WriteAllTextAsync(Path.Combine(mockDocsDir, "logo.svg"), logo);
        RunGitCommand("add .", _mockRepoPath);
        RunGitCommand(
            "-c user.name=TestUser -c user.email=test@test.com -c commit.gpgsign=false commit --no-gpg-sign --amend -m \"Initial commit\"",
            _mockRepoPath);
        await syncJob.ExecuteAsync();
        await using var db = new InMemoryContext(options);
        var job = new IndexDocumentsJob(db, rootProvider,
            new NavConfigParser(NullLogger<NavConfigParser>.Instance), folders, cache,
            loggerFactory.CreateLogger<IndexDocumentsJob>());
        await job.ExecuteAsync();

        var document = await db.Documents.SingleAsync();
        var firstContent = document.Content;
        var workspace = folders.GetWorkspaceFolder();
        var firstImage = Directory.GetFiles(Path.Combine(workspace, "doc-images")).Single();
        var firstImageUrl = $"doc-images/{Path.GetFileName(firstImage)}";
        Assert.Contains($"![alt]({firstImageUrl})", firstContent);
        const string legacyImageUrl = "doc-images/0123456789abcdef.svg";
        const string translatedMarkdown = "# Test traduit\n![Image traduite](" + legacyImageUrl + ")";
        var translatedAt = document.FileLastModified.AddHours(1);
        document.Content = firstContent.Replace(firstImageUrl, legacyImageUrl, StringComparison.Ordinal);
        document.SourceCulture = "en-US";
        var translation = new LocalizedDocument
        {
            DocumentId = document.Id,
            Culture = "fr-FR",
            LocalizedTitle = "Test traduit",
            LocalizedContent = translatedMarkdown,
            LastLocalizedAt = translatedAt
        };
        db.LocalizedDocuments.Add(translation);
        await db.SaveChangesAsync();

        // Simulate upgrading an existing database while recreating the checkout.
        var checkout = Path.Combine(rootProvider.GetStorageRootPath(), "repo");
        var checkoutImage = Path.Combine(checkout, "Docs", "test_category", "logo.svg");
        var oldFileTime = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(checkoutImage, oldFileTime);
        Directory.Move(checkout, Path.Combine(_tempPath, "previous-repo"));
        await syncJob.ExecuteAsync();
        Assert.AreNotEqual(oldFileTime, File.GetLastWriteTimeUtc(checkoutImage));
        await job.ExecuteAsync();
        db.ChangeTracker.Clear();

        document = await db.Documents.SingleAsync();
        translation = await db.LocalizedDocuments.SingleAsync();
        Assert.AreEqual(firstContent, document.Content);
        Assert.AreEqual("en-US", document.SourceCulture);
        Assert.AreEqual("Test traduit", translation.LocalizedTitle);
        Assert.AreEqual(translatedAt, translation.LastLocalizedAt);
        Assert.AreEqual(translatedMarkdown.Replace(legacyImageUrl, firstImageUrl, StringComparison.Ordinal),
            translation.LocalizedContent);

        // A same-length image edit with the same filesystem timestamp still changes the URL.
        const string newLogo = "<svg>icon</svg>";
        var checkoutImageTime = File.GetLastWriteTimeUtc(checkoutImage);
        await File.WriteAllTextAsync(checkoutImage, newLogo);
        File.SetLastWriteTimeUtc(checkoutImage, checkoutImageTime);
        await job.ExecuteAsync();
        db.ChangeTracker.Clear();
        var newImage = Directory.GetFiles(Path.Combine(workspace, "doc-images")).Single(path => path != firstImage);
        var newImageUrl = $"doc-images/{Path.GetFileName(newImage)}";
        Assert.AreEqual(newLogo, await File.ReadAllTextAsync(newImage));
        Assert.AreEqual(logo, await File.ReadAllTextAsync(firstImage));
        Assert.Contains(newImageUrl, (await db.Documents.SingleAsync()).Content);
        Assert.Contains(newImageUrl, (await db.LocalizedDocuments.SingleAsync()).LocalizedContent);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Strict context: throws immediately if SaveChangesAsync is called with
    // any pending writes (Added / Modified / Deleted entries).
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class NoWriteDbContext(DbContextOptions<InMemoryContext> options)
        : InMemoryContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var dirty = ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added
                                    or EntityState.Modified
                                    or EntityState.Deleted)
                .ToList();

            if (dirty.Count > 0)
            {
                var details = string.Join(", ",
                    dirty.Select(e => $"{e.Entity.GetType().Name}({e.State})"));
                throw new InvalidOperationException(
                    $"Second IndexDocumentsJob run attempted to write {dirty.Count} change(s): {details}. " +
                    "The incremental sync should have detected no changes and skipped all documents.");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
