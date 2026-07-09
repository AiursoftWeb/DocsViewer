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
public class SyncDocsRepoJobTests
{
    private string _tempPath = null!;
    private string _mockRepoPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "SyncJobTest_" + Guid.NewGuid());
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

    [TestMethod]
    public async Task ExecuteAsync_ClonesRepoToCorrectPath()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Storage:Path", _tempPath },
                // Supply via config so GlobalSettingsService returns early — no DB access needed.
                { $"GlobalSettings:{SettingsMap.DocsRepoUrl}", _mockRepoPath }
            })
            .Build();

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var envMock = new Mock<IWebHostEnvironment>();
        envMock.Setup(e => e.ContentRootPath).Returns(_tempPath);
        var rootProvider = new StorageRootPathProvider(config);
        var foldersProvider = new FeatureFoldersProvider(rootProvider);
        var fileLockProvider = new FileLockProvider(memoryCache);
        var storageService = new StorageService(foldersProvider, fileLockProvider, new EphemeralDataProtectionProvider());

        var dbOptions = new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase("SyncJobTest_" + Guid.NewGuid())
            .Options;
        await using var db = new InMemoryContext(dbOptions);

        var globalSettings = new GlobalSettingsService(db, config, storageService, memoryCache);

        var sp = new ServiceCollection()
            .AddLogging()
            .AddGitRunner()
            .BuildServiceProvider();
        var workspaceManager = sp.GetRequiredService<WorkspaceManager>();

        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SyncDocsRepoJob>();
        var job = new SyncDocsRepoJob(globalSettings, workspaceManager, logger, envMock.Object);

        // Act
        await job.ExecuteAsync();

        // Assert
        var expectedRepoPath = Path.Combine(_tempPath, "App_Data", "DocsRepo");
        Assert.IsTrue(
            Directory.Exists(expectedRepoPath),
            $"Repo directory should exist at: {expectedRepoPath}");
        Assert.IsTrue(
            Directory.Exists(Path.Combine(expectedRepoPath, ".git")),
            "Cloned repo should contain a .git directory");
    }

    [TestMethod]
    public async Task ExecuteAsync_ResetsRepoWhenAlreadyCloned()
    {
        // Arrange — first clone
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Storage:Path", _tempPath },
                { $"GlobalSettings:{SettingsMap.DocsRepoUrl}", _mockRepoPath }
            })
            .Build();

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var envMock = new Mock<IWebHostEnvironment>();
        envMock.Setup(e => e.ContentRootPath).Returns(_tempPath);
        var rootProvider = new StorageRootPathProvider(config);
        var foldersProvider = new FeatureFoldersProvider(rootProvider);
        var fileLockProvider = new FileLockProvider(memoryCache);
        var storageService = new StorageService(foldersProvider, fileLockProvider, new EphemeralDataProtectionProvider());

        var dbOptions = new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase("SyncJobTest_" + Guid.NewGuid())
            .Options;
        await using var db = new InMemoryContext(dbOptions);
        var globalSettings = new GlobalSettingsService(db, config, storageService, memoryCache);

        var sp = new ServiceCollection()
            .AddLogging()
            .AddGitRunner()
            .BuildServiceProvider();
        var workspaceManager = sp.GetRequiredService<WorkspaceManager>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SyncDocsRepoJob>();
        var job = new SyncDocsRepoJob(globalSettings, workspaceManager, logger, envMock.Object);

        await job.ExecuteAsync(); // first run — clones

        // Dirty the working tree with a stray file
        var strayFile = Path.Combine(_tempPath, "App_Data", "DocsRepo", "stray-test-file.txt");
        await File.WriteAllTextAsync(strayFile, "should be cleaned up");

        // Act — second run should reset/clean the repo
        await job.ExecuteAsync();

        // Assert — stray file removed, repo still valid
        Assert.IsFalse(File.Exists(strayFile), "Stray file should have been removed by git clean");
        Assert.IsTrue(Directory.Exists(Path.Combine(_tempPath, "App_Data", "DocsRepo", ".git")));
    }
}
