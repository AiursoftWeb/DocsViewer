using System.Collections.Concurrent;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Services.BackgroundJobs;
using Aiursoft.DocsViewer.Services.FileStorage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public class LocalizeNavTitlesJobTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Fake translation service: records every call so we can assert that labels
    // are translated ONE AT A TIME (never concatenated) and never re-translated.
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class CountingTranslationService : IDocumentTranslationService
    {
        public readonly ConcurrentBag<(string Text, string Lang)> Calls = [];

        public Task<string> TranslateAsync(string text, string targetLanguage)
        {
            Calls.Add((text, targetLanguage));
            return Task.FromResult($"[{targetLanguage}] {text}");
        }
    }

    // Test double: stub out the AI source-culture detection.
    private sealed class TestableJob(
        DocsViewerDbContext db,
        StorageRootPathProvider storageRootPathProvider,
        GlobalSettingsService settings,
        NavConfigParser navParser,
        IDocumentTranslationService translator,
        string? detectedCulture)
        : LocalizeNavTitlesJob(db, storageRootPathProvider, settings, navParser, translator, null!, null!,
            NullLogger<LocalizeNavTitlesJob>.Instance)
    {
        public int DetectCalls;

        protected override Task<string?> DetectNavSourceCultureAsync(List<string> titles)
        {
            DetectCalls++;
            return Task.FromResult(detectedCulture);
        }
    }

    private sealed class SqliteTestContext(DbContextOptions<SqliteTestContext> options)
        : DocsViewerDbContext(options);

    private SqliteConnection _connection = null!;
    private DbContextOptions<SqliteTestContext> _dbOptions = null!;
    private IMemoryCache _cache = null!;
    private string _repoRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<SqliteTestContext>()
            .UseSqlite(_connection)
            .Options;
        _cache = new MemoryCache(new MemoryCacheOptions());

        using var db = new SqliteTestContext(_dbOptions);
        db.Database.EnsureCreated();

        // Storage:Path/repo/properdocs.yml
        _repoRoot = Path.Combine(Path.GetTempPath(), "navjob_" + Guid.NewGuid().ToString("N"));
        var docsRepo = Path.Combine(_repoRoot, "repo");
        Directory.CreateDirectory(docsRepo);
        File.WriteAllText(Path.Combine(docsRepo, "properdocs.yml"),
            """
            docs_dir: Docs
            nav:
              - Home: index.md
              - Getting Started:
                  - Installation: install.md
                  - Configuration: config.md
              - Advanced:
                  - Deep:
                      - Internals: internals.md
              - External Link: https://example.com/
            """);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _connection.Close();
        _connection.Dispose();
        if (Directory.Exists(_repoRoot)) Directory.Delete(_repoRoot, recursive: true);
    }

    // Branch (group) titles and external links in the yaml above: "Getting Started", "Advanced", "Deep", "External Link".
    private static readonly string[] GroupTitles = ["Getting Started", "Advanced", "Deep", "External Link"];

    private async Task<(TestableJob Job, CountingTranslationService Translator)> CreateJobAsync(
        string languages,
        string? detectedCulture,
        string openAiInstance = "http://localhost:11434")
    {
        await using (var seedDb = new SqliteTestContext(_dbOptions))
        {
            seedDb.GlobalSettings.AddRange(
                new GlobalSetting { Key = SettingsMap.LocalizationLanguages, Value = languages },
                new GlobalSetting { Key = SettingsMap.OpenAiInstance, Value = openAiInstance },
                new GlobalSetting { Key = SettingsMap.OpenAiLocalizationModel, Value = "test-model" },
                new GlobalSetting { Key = SettingsMap.OpenAiApiToken, Value = "test-token" });
            await seedDb.SaveChangesAsync();
        }

        var db = new SqliteTestContext(_dbOptions);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "Storage:Path", _repoRoot }
        }).Build();
        var settings = new GlobalSettingsService(db, config, null!, _cache);
        var navParser = new NavConfigParser(NullLogger<NavConfigParser>.Instance);
        var translator = new CountingTranslationService();
        var storageRootPathProvider = new StorageRootPathProvider(config);

        var job = new TestableJob(db, storageRootPathProvider, settings, navParser, translator, detectedCulture);
        return (job, translator);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // First run: every group title is translated for a non-source target culture.
    // ═════════════════════════════════════════════════════════════════════════
    [TestMethod]
    public async Task ExecuteAsync_TranslatesAllGroupTitles_OnFirstRun()
    {
        var (job, translator) = await CreateJobAsync(languages: "en-US", detectedCulture: "zh-CN");

        await job.ExecuteAsync();

        await using var assertDb = new SqliteTestContext(_dbOptions);
        var rows = await assertDb.LocalizedNavTitles.Where(t => t.Culture == "en-US").ToListAsync();

        CollectionAssert.AreEquivalent(GroupTitles, rows.Select(r => r.SourceText).ToArray());
        foreach (var r in rows)
            Assert.AreEqual($"[en-US] {r.SourceText}", r.LocalizedText);

        // Detection ran exactly once; each label translated on its own (never concatenated).
        Assert.AreEqual(1, job.DetectCalls);
        Assert.AreEqual(GroupTitles.Length, translator.Calls.Count);
        CollectionAssert.AreEquivalent(GroupTitles, translator.Calls.Select(c => c.Text).ToArray());
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Lazy: existing (label × culture) pairs are skipped — no re-translation,
    // and detection is not even invoked when nothing is missing.
    // ═════════════════════════════════════════════════════════════════════════
    [TestMethod]
    public async Task ExecuteAsync_SkipsExistingPairs_AndDoesNotDetectWhenNothingMissing()
    {
        await using (var seed = new SqliteTestContext(_dbOptions))
        {
            foreach (var t in GroupTitles)
                seed.LocalizedNavTitles.Add(new LocalizedNavTitle
                {
                    SourceText = t, Culture = "en-US", LocalizedText = "PRE-EXISTING",
                    LastLocalizedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                });
            await seed.SaveChangesAsync();
        }

        var (job, translator) = await CreateJobAsync(languages: "en-US", detectedCulture: "zh-CN");
        await job.ExecuteAsync();

        await using var assertDb = new SqliteTestContext(_dbOptions);
        var rows = await assertDb.LocalizedNavTitles.Where(t => t.Culture == "en-US").ToListAsync();

        Assert.AreEqual(GroupTitles.Length, rows.Count, "No new rows should be created.");
        Assert.IsTrue(rows.All(r => r.LocalizedText == "PRE-EXISTING"), "Existing rows must be untouched.");
        Assert.AreEqual(0, translator.Calls.Count, "No AI translation calls for already-present pairs.");
        Assert.AreEqual(0, job.DetectCalls, "Detection must be skipped when nothing is missing.");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Pass-through: when the target culture equals the detected source culture,
    // the label is stored as-is with no AI call.
    // ═════════════════════════════════════════════════════════════════════════
    [TestMethod]
    public async Task ExecuteAsync_PassThrough_WhenCultureEqualsSource()
    {
        var (job, translator) = await CreateJobAsync(languages: "en-US", detectedCulture: "en-US");

        await job.ExecuteAsync();

        await using var assertDb = new SqliteTestContext(_dbOptions);
        var rows = await assertDb.LocalizedNavTitles.Where(t => t.Culture == "en-US").ToListAsync();

        CollectionAssert.AreEquivalent(GroupTitles, rows.Select(r => r.SourceText).ToArray());
        foreach (var r in rows)
            Assert.AreEqual(r.SourceText, r.LocalizedText, "Pass-through stores the original text.");
        Assert.AreEqual(0, translator.Calls.Count, "Pass-through must not call the AI translator.");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Adding a new culture only translates the missing pairs, not existing ones.
    // ═════════════════════════════════════════════════════════════════════════
    [TestMethod]
    public async Task ExecuteAsync_OnlyTranslatesMissingCulture()
    {
        await using (var seed = new SqliteTestContext(_dbOptions))
        {
            foreach (var t in GroupTitles)
                seed.LocalizedNavTitles.Add(new LocalizedNavTitle
                {
                    SourceText = t, Culture = "en-US", LocalizedText = $"[en-US] {t}",
                    LastLocalizedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                });
            await seed.SaveChangesAsync();
        }

        var (job, translator) = await CreateJobAsync(languages: "en-US,ja-JP", detectedCulture: "zh-CN");
        await job.ExecuteAsync();

        await using var assertDb = new SqliteTestContext(_dbOptions);
        var ja = await assertDb.LocalizedNavTitles.Where(t => t.Culture == "ja-JP").ToListAsync();

        CollectionAssert.AreEquivalent(GroupTitles, ja.Select(r => r.SourceText).ToArray());
        // Only the 3 ja-JP pairs were translated; the 3 pre-existing en-US pairs were skipped.
        Assert.AreEqual(GroupTitles.Length, translator.Calls.Count);
        Assert.IsTrue(translator.Calls.All(c => c.Lang == "ja-JP"));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // AI disabled → whole job is skipped, no detection, no rows.
    // ═════════════════════════════════════════════════════════════════════════
    [TestMethod]
    public async Task ExecuteAsync_SkipsWhenAiDisabled()
    {
        var (job, translator) = await CreateJobAsync(languages: "en-US", detectedCulture: "zh-CN", openAiInstance: "");

        await job.ExecuteAsync();

        await using var assertDb = new SqliteTestContext(_dbOptions);
        Assert.IsFalse(await assertDb.LocalizedNavTitles.AnyAsync());
        Assert.AreEqual(0, translator.Calls.Count);
        Assert.AreEqual(0, job.DetectCalls);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // No languages configured → skipped.
    // ═════════════════════════════════════════════════════════════════════════
    [TestMethod]
    public async Task ExecuteAsync_SkipsWhenNoLanguagesConfigured()
    {
        var (job, translator) = await CreateJobAsync(languages: "", detectedCulture: "zh-CN");

        await job.ExecuteAsync();

        await using var assertDb = new SqliteTestContext(_dbOptions);
        Assert.IsFalse(await assertDb.LocalizedNavTitles.AnyAsync());
        Assert.AreEqual(0, translator.Calls.Count);
    }
}
