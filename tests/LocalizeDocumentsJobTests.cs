using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Services.BackgroundJobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public class LocalizeDocumentsJobTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Fake translation service: returns predictable translations.
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class FakeTranslationService : IDocumentTranslationService
    {
        public Task<string> TranslateAsync(string text, string targetLanguage)
        {
            return Task.FromResult($"[{targetLanguage}] {text}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SQLite in-memory context (InMemory provider doesn't support complex queries)
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class SqliteTestContext(DbContextOptions<SqliteTestContext> options)
        : DocsViewerDbContext(options)
    {
    }

    private SqliteConnection _connection = null!;
    private DbContextOptions<SqliteTestContext> _dbOptions = null!;
    private IMemoryCache _cache = null!;

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
    }

    [TestCleanup]
    public void Cleanup()
    {
        _connection.Close();
        _connection.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helper: build a configured LocalizeDocumentsJob
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<LocalizeDocumentsJob> CreateJobAsync(
        string languages = "en-US",
        string openAiInstance = "http://localhost:11434")
    {
        // Seed settings into a fresh context
        await using (var seedDb = new SqliteTestContext(_dbOptions))
        {
            seedDb.GlobalSettings.Add(new GlobalSetting
            {
                Key = SettingsMap.LocalizationLanguages,
                Value = languages
            });
            seedDb.GlobalSettings.Add(new GlobalSetting
            {
                Key = SettingsMap.OpenAiInstance,
                Value = openAiInstance
            });
            seedDb.GlobalSettings.Add(new GlobalSetting
            {
                Key = SettingsMap.OpenAiLocalizationModel,
                Value = "test-model"
            });
            seedDb.GlobalSettings.Add(new GlobalSetting
            {
                Key = SettingsMap.OpenAiApiToken,
                Value = "test-token"
            });
            await seedDb.SaveChangesAsync();
        }

        // Create a new DB context for the job (owns its own context)
        var db = new SqliteTestContext(_dbOptions);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var settings = new GlobalSettingsService(db, config, null!, _cache);
        var translator = new FakeTranslationService();

        return new LocalizeDocumentsJob(
            db,
            settings,
            translator,
            NullLogger<LocalizeDocumentsJob>.Instance);
    }

    private static async Task SeedAsync(DocsViewerDbContext db, params object[] entities)
    {
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private static Document CreateDocument(int id, string title, DateTime fileLastModified,
        string content = "Test content")
    {
        return new Document
        {
            Id = id,
            Title = title,
            Category = "test",
            FilePath = $"docs/test/{id}.md",
            Content = content,
            FileLastModified = fileLastModified
        };
    }

    private static LocalizedDocument CreateLocalized(int id, int documentId, string culture,
        DateTime lastLocalizedAt,
        string title = "Old translated title",
        string content = "Old translated content")
    {
        return new LocalizedDocument
        {
            Id = id,
            DocumentId = documentId,
            Culture = culture,
            LocalizedTitle = title,
            LocalizedContent = content,
            LastLocalizedAt = lastLocalizedAt
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Test 1: Stale translation → cleared and re-translated
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ExecuteAsync_ReLocalizesWhenSourceUpdated()
    {
        // Arrange: document was updated after the last localization
        var sourceUpdateTime = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var lastLocalizedTime = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc); // 1 day older

        var document = CreateDocument(1, "西红柿炒鸡蛋", sourceUpdateTime);
        var oldLocalized = CreateLocalized(1, 1, "en-US", lastLocalizedTime,
            title: "Old Title",
            content: "Old Content");

        var job = await CreateJobAsync(languages: "en-US");
        await using var seedDb = new SqliteTestContext(_dbOptions);
        await SeedAsync(seedDb, document, oldLocalized);

        // Act
        await job.ExecuteAsync();

        // Assert: the localized content must be from the fake translator,
        // NOT the old content.
        await using var assertDb = new SqliteTestContext(_dbOptions);
        var localized = await assertDb.LocalizedDocuments
            .FirstAsync(lr => lr.DocumentId == 1 && lr.Culture == "en-US");

        Assert.AreEqual("[en-US] 西红柿炒鸡蛋", localized.LocalizedTitle,
            "Title must be re-translated, not the old value.");
        Assert.AreEqual("[en-US] Test content", localized.LocalizedContent,
            "Content must be re-translated.");
        Assert.AreNotEqual(DateTime.MinValue, localized.LastLocalizedAt,
            "LastLocalizedAt must be updated to a current timestamp.");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Test 2: Current translation → skipped entirely
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ExecuteAsync_SkipsDocumentWhenTranslationIsCurrent()
    {
        // Arrange: translation was done AFTER the source was last modified
        var sourceUpdateTime = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);
        var lastLocalizedTime = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc); // newer than source

        var document = CreateDocument(1, "西红柿炒鸡蛋", sourceUpdateTime);
        var currentLocalized = CreateLocalized(1, 1, "en-US", lastLocalizedTime,
            title: "Current Title",
            content: "Current Content");

        var job = await CreateJobAsync(languages: "en-US");
        await using var seedDb = new SqliteTestContext(_dbOptions);
        await SeedAsync(seedDb, document, currentLocalized);

        // Act
        await job.ExecuteAsync();

        // Assert: the localized content must be UNCHANGED
        await using var assertDb = new SqliteTestContext(_dbOptions);
        var localized = await assertDb.LocalizedDocuments
            .FirstAsync(lr => lr.DocumentId == 1 && lr.Culture == "en-US");

        Assert.AreEqual("Current Title", localized.LocalizedTitle,
            "Title must remain unchanged when translation is already current.");
        Assert.AreEqual("Current Content", localized.LocalizedContent);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Test 3: New document with no existing localization → fresh translation
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ExecuteAsync_LocalizesNewDocumentWithNoExistingTranslation()
    {
        // Arrange: document exists but has NO LocalizedDocument row
        var sourceUpdateTime = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var document = CreateDocument(1, "新菜谱", sourceUpdateTime);

        var job = await CreateJobAsync(languages: "en-US");
        await using var seedDb = new SqliteTestContext(_dbOptions);
        await SeedAsync(seedDb, document);

        // Act
        await job.ExecuteAsync();

        // Assert: a new LocalizedDocument was created with fresh translations
        await using var assertDb = new SqliteTestContext(_dbOptions);
        var localized = await assertDb.LocalizedDocuments
            .FirstOrDefaultAsync(lr => lr.DocumentId == 1 && lr.Culture == "en-US");

        Assert.IsNotNull(localized, "A new LocalizedDocument row must be created.");
        Assert.AreEqual("[en-US] 新菜谱", localized.LocalizedTitle);
        Assert.AreNotEqual(DateTime.MinValue, localized.LastLocalizedAt,
            "LastLocalizedAt must be set after successful translation.");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Test 4: Multiple cultures — each gets its own localization
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ExecuteAsync_LocalizesMultipleCultures()
    {
        var sourceUpdateTime = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var document = CreateDocument(1, "西红柿炒鸡蛋", sourceUpdateTime);

        var job = await CreateJobAsync(languages: "en-US,ja-JP");
        await using var seedDb = new SqliteTestContext(_dbOptions);
        await SeedAsync(seedDb, document);

        // Act
        await job.ExecuteAsync();

        // Assert: both cultures get translations
        await using var assertDb = new SqliteTestContext(_dbOptions);
        var enRow = await assertDb.LocalizedDocuments
            .FirstOrDefaultAsync(lr => lr.DocumentId == 1 && lr.Culture == "en-US");
        var jaRow = await assertDb.LocalizedDocuments
            .FirstOrDefaultAsync(lr => lr.DocumentId == 1 && lr.Culture == "ja-JP");

        Assert.IsNotNull(enRow, "en-US translation must exist.");
        Assert.IsNotNull(jaRow, "ja-JP translation must exist.");
        Assert.AreEqual("[en-US] 西红柿炒鸡蛋", enRow.LocalizedTitle);
        Assert.AreEqual("[ja-JP] 西红柿炒鸡蛋", jaRow.LocalizedTitle);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Test 5: AI disabled → entire job is skipped
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ExecuteAsync_SkipsWhenAiDisabled()
    {
        var sourceUpdateTime = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var document = CreateDocument(1, "西红柿炒鸡蛋", sourceUpdateTime);

        // OpenAiInstance is empty → AI localization is disabled
        var job = await CreateJobAsync(languages: "en-US", openAiInstance: "");
        await using var seedDb = new SqliteTestContext(_dbOptions);
        await SeedAsync(seedDb, document);

        // Act
        await job.ExecuteAsync();

        // Assert: no localization was created
        await using var assertDb = new SqliteTestContext(_dbOptions);
        var anyLocalized = await assertDb.LocalizedDocuments.AnyAsync();
        Assert.IsFalse(anyLocalized, "No localization should be created when AI is disabled.");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Test 6: Source updated → only changed document fields are re-translated
    //         (unchanged fields keep their old translations... wait, no!
    //          With our fix, ALL fields are cleared and re-translated when
    //          FileLastModified > LastLocalizedAt — verifying that here.)
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ExecuteAsync_ClearsAndReTranslatesAllFieldsWhenSourceUpdated()
    {
        // Arrange: source was updated, but the old localization still exists.
        // Only SOME fields changed in the source (e.g. Name changed, Steps changed).
        // But ALL localized fields must be refreshed regardless.
        var sourceUpdateTime = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var lastLocalizedTime = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

        // Source has different content than what was previously translated
        var document = CreateDocument(1, "Updated Title", sourceUpdateTime,
            content: "Updated Content");

        // Old localization had different (stale) content
        var oldLocalized = CreateLocalized(1, 1, "en-US", lastLocalizedTime,
            title: "Stale Title",
            content: "Stale Content");

        var job = await CreateJobAsync(languages: "en-US");
        await using var seedDb = new SqliteTestContext(_dbOptions);
        await SeedAsync(seedDb, document, oldLocalized);

        // Act
        await job.ExecuteAsync();

        // Assert: EVERY field reflects the fake translator output
        // (which means they were cleared and re-translated, not left as stale content)
        await using var assertDb = new SqliteTestContext(_dbOptions);
        var localized = await assertDb.LocalizedDocuments
            .FirstAsync(lr => lr.DocumentId == 1 && lr.Culture == "en-US");

        Assert.AreEqual("[en-US] Updated Title", localized.LocalizedTitle);
        Assert.AreEqual("[en-US] Updated Content", localized.LocalizedContent);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Test 7: No languages configured → job is skipped
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ExecuteAsync_SkipsWhenNoLanguagesConfigured()
    {
        var sourceUpdateTime = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var document = CreateDocument(1, "西红柿炒鸡蛋", sourceUpdateTime);

        var job = await CreateJobAsync(languages: "");
        await using var seedDb = new SqliteTestContext(_dbOptions);
        await SeedAsync(seedDb, document);

        // Act
        await job.ExecuteAsync();

        // Assert: nothing was localized
        await using var assertDb = new SqliteTestContext(_dbOptions);
        var anyLocalized = await assertDb.LocalizedDocuments.AnyAsync();
        Assert.IsFalse(anyLocalized, "No localization when languages are empty.");
    }
}
