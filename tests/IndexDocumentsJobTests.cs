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
using Moq;

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
            new InMemoryContext(dbOptions), config, storageService, memoryCache);

        var workspaceManager = sp.GetRequiredService<WorkspaceManager>();
        var syncJob = new SyncDocsRepoJob(
            globalSettings, workspaceManager, rootProvider, loggerFactory.CreateLogger<SyncDocsRepoJob>());

        return (syncJob, rootProvider, globalSettings, loggerFactory, dbOptions, foldersProvider, storageService, memoryCache);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test
    // ─────────────────────────────────────────────────────────────────────────

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
                db, storageRootPathProvider, new NavConfigParser(Mock.Of<ILogger<NavConfigParser>>()), foldersProvider, memCache,
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
            strictDb, storageRootPathProvider, new NavConfigParser(Mock.Of<ILogger<NavConfigParser>>()), foldersProvider, memCache,
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
                db, storageRootPathProvider, new NavConfigParser(Mock.Of<ILogger<NavConfigParser>>()), foldersProvider, memCache,
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
                db, storageRootPathProvider, new NavConfigParser(Mock.Of<ILogger<NavConfigParser>>()), foldersProvider, memCache,
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
                db, storageRootPathProvider, new NavConfigParser(Mock.Of<ILogger<NavConfigParser>>()), foldersProvider, memCache,
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

    // ─────────────────────────────────────────────────────────────────────────
    // ComputeImageFingerprint determinism tests
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A minimal subclass that exposes the protected ComputeImageFingerprint so tests
    /// can verify its behaviour without going through the full indexing pipeline.
    /// </summary>
    private sealed class TestableIndexDocumentsJob(
        DocsViewerDbContext db,
        StorageRootPathProvider storageRootPathProvider,
        NavConfigParser navConfigParser,
        FeatureFoldersProvider featureFolders,
        IMemoryCache cache,
        ILogger<IndexDocumentsJob> logger)
        : IndexDocumentsJob(db, storageRootPathProvider, navConfigParser, featureFolders, cache, logger)
    {
        public new string ComputeImageFingerprint(string relativePath, string absolutePath)
            => base.ComputeImageFingerprint(relativePath, absolutePath);
    }

    private static TestableIndexDocumentsJob CreateTestableJob(string tempPath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Storage:Path", tempPath }
            })
            .Build();

        var rootProvider = new StorageRootPathProvider(config);
        var folders = new FeatureFoldersProvider(rootProvider);
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var dbOptions = new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase("FingerprintTest_" + Guid.NewGuid())
            .Options;
        var db = new InMemoryContext(dbOptions);

        var loggerFactory = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider()
            .GetRequiredService<ILoggerFactory>();

        return new TestableIndexDocumentsJob(
            db, rootProvider,
            new NavConfigParser(Mock.Of<ILogger<NavConfigParser>>()),
            folders, memCache,
            loggerFactory.CreateLogger<IndexDocumentsJob>());
    }

    [TestMethod]
    public void ComputeImageFingerprint_SameFile_SameFingerprint()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "FingerprintTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var imagePath = Path.Combine(tempDir, "logo.svg");
            File.WriteAllText(imagePath, "<svg>test</svg>");
            var job = CreateTestableJob(tempDir);

            // Act
            var fp1 = job.ComputeImageFingerprint("Assets/logo.svg", imagePath);
            var fp2 = job.ComputeImageFingerprint("Assets/logo.svg", imagePath);

            // Assert
            Assert.AreEqual(fp1, fp2, "Same file must produce the same fingerprint.");
            Assert.AreEqual(16, fp1.Length, "Fingerprint must be 16 hex chars.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ComputeImageFingerprint_DifferentPath_DifferentFingerprint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FingerprintTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var image1 = Path.Combine(tempDir, "a.svg");
            var image2 = Path.Combine(tempDir, "b.svg");
            var content = "<svg>test</svg>";
            File.WriteAllText(image1, content);
            File.WriteAllText(image2, content);
            var job = CreateTestableJob(tempDir);

            var fp1 = job.ComputeImageFingerprint("dir/a.svg", image1);
            var fp2 = job.ComputeImageFingerprint("dir/b.svg", image2);

            Assert.AreNotEqual(fp1, fp2,
                "Different paths must produce different fingerprints.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ComputeImageFingerprint_DifferentContent_DifferentFingerprint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FingerprintTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var imagePath = Path.Combine(tempDir, "icon.png");
            File.WriteAllText(imagePath, "aaaa"); // small content to force different size
            var job1 = CreateTestableJob(tempDir);
            var fp1 = job1.ComputeImageFingerprint("icon.png", imagePath);

            File.WriteAllText(imagePath, "aaaabbbbccccddddeeeeffff"); // different size
            var job2 = CreateTestableJob(tempDir);
            var fp2 = job2.ComputeImageFingerprint("icon.png", imagePath);

            Assert.AreNotEqual(fp1, fp2,
                "Different file sizes must produce different fingerprints.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComputeImageFingerprint_RerunOnSameImage_ImagePathsStable()
    {
        // End-to-end: create a doc with an image, index it twice, verify the
        // image path in the stored Content does not change across runs.
        var dbName = "IndexJobTest_" + Guid.NewGuid();
        var (syncJob, storageRootPathProvider, _, loggerFactory, dbOptions, foldersProvider, _, memCache) = BuildServices(dbName);

        // Create an image in the mock repo Docs dir
        var mockDocsDir = Path.Combine(_mockRepoPath, "Docs", "test_category");
        File.WriteAllText(Path.Combine(mockDocsDir, "test_doc.md"),
            "# Test Doc\n![alt](logo.svg)\nThis is a test.");
        File.WriteAllText(Path.Combine(mockDocsDir, "logo.svg"), "<svg>logo</svg>");
        // Amend the commit so git log returns a stable date
        RunGitCommand("add .", _mockRepoPath);
        RunGitCommand(
            "-c user.name=TestUser -c user.email=test@test.com -c commit.gpgsign=false commit --no-gpg-sign --amend -m \"Initial commit\"",
            _mockRepoPath);

        // First run
        await syncJob.ExecuteAsync();
        string contentAfterFirst;
        await using (var db = new InMemoryContext(dbOptions))
        {
            var job = new IndexDocumentsJob(
                db, storageRootPathProvider, new NavConfigParser(Mock.Of<ILogger<NavConfigParser>>()), foldersProvider, memCache,
                loggerFactory.CreateLogger<IndexDocumentsJob>());
            await job.ExecuteAsync();
            contentAfterFirst = (await db.Documents.FirstAsync()).Content;
        }
        Assert.IsTrue(contentAfterFirst.Contains("doc-images/"),
            "Content must contain a rewritten image path after first indexing.");

        // Second run: content must be exactly the same — image UUID must not change
        await using (var db = new InMemoryContext(dbOptions))
        {
            var job = new IndexDocumentsJob(
                db, storageRootPathProvider, new NavConfigParser(Mock.Of<ILogger<NavConfigParser>>()), foldersProvider, memCache,
                loggerFactory.CreateLogger<IndexDocumentsJob>());
            await job.ExecuteAsync();
            var contentAfterSecond = (await db.Documents.FirstAsync()).Content;
            Assert.AreEqual(contentAfterFirst, contentAfterSecond,
                "Image path must be stable across IndexDocumentsJob reruns.");
        }
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
