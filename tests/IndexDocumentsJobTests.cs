using Aiursoft.GitRunner;
using Aiursoft.DocsViewer.Configuration;
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
        var docsPath = Path.Combine(path, "docs", "test_category");
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
        IWebHostEnvironment env,
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
                { $"GlobalSettings:{SettingsMap.DocsRepoUrl}", _mockRepoPath },
                { $"GlobalSettings:{SettingsMap.DocsRootPath}", "/" }
            })
            .Build();

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var envMock = new Mock<IWebHostEnvironment>();
        envMock.Setup(e => e.ContentRootPath).Returns(_tempPath);
        
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
            globalSettings, workspaceManager, loggerFactory.CreateLogger<SyncDocsRepoJob>(), envMock.Object);

        return (syncJob, envMock.Object, globalSettings, loggerFactory, dbOptions, foldersProvider, storageService, memoryCache);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IndexDocumentsJob_SecondRun_WritesNothing()
    {
        var dbName = "IndexJobTest_" + Guid.NewGuid();
        var (syncJob, env, globalSettings, loggerFactory, dbOptions, foldersProvider, storageService, memCache) = BuildServices(dbName);

        // ── Step 1: sync git repo ────────────────────────────────────────────
        await syncJob.ExecuteAsync();

        var repoPath = Path.Combine(_tempPath, "App_Data", "DocsRepo");
        Assert.IsTrue(Directory.Exists(repoPath), "Repo must exist before indexing");

        // ── Step 2: first index run — should insert documents ──────────────────
        await using (var db = new InMemoryContext(dbOptions))
        {
            var job = new IndexDocumentsJob(
                db, env, globalSettings, foldersProvider, memCache,
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
            strictDb, env, globalSettings, foldersProvider, memCache,
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
        var (syncJob, env, globalSettings, loggerFactory, dbOptions, foldersProvider, storageService, memCache) = BuildServices(dbName);

        // ── Step 1: sync and index normally ────────────────────────────────────
        await syncJob.ExecuteAsync();

        await using (var db = new InMemoryContext(dbOptions))
        {
            var job = new IndexDocumentsJob(
                db, env, globalSettings, foldersProvider, memCache,
                loggerFactory.CreateLogger<IndexDocumentsJob>());
            await job.ExecuteAsync();
        }

        // ── Step 2: simulate pre-feature state by changing Title ────
        await using (var db = new InMemoryContext(dbOptions))
        {
            var document = await db.Documents.FirstAsync();
            Assert.AreEqual("test_doc", document.Title,
                "First run must parse and store title value.");

            document.Title = "Old Title";
            await db.SaveChangesAsync();
        }

        // ── Step 3: re-run index — must restore the title value ─────────────
        await using (var db = new InMemoryContext(dbOptions))
        {
            var job = new IndexDocumentsJob(
                db, env, globalSettings, foldersProvider, memCache,
                loggerFactory.CreateLogger<IndexDocumentsJob>());
            await job.ExecuteAsync();
        }

        await using (var db = new InMemoryContext(dbOptions))
        {
            var document = await db.Documents.FirstAsync();
            Assert.AreEqual("test_doc", document.Title,
                "Second run must re-index and restore title value for documents.");
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
