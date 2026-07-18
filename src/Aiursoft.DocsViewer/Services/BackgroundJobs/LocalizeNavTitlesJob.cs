using System.Globalization;
using Aiursoft.Canon;
using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.GptClient.Abstractions;
using Aiursoft.GptClient.Services;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

/// <summary>
/// Translates the sidebar navigation group/folder labels (the branch <c>Title</c>s in the repo's
/// <c>properdocs.yml</c> <c>nav:</c> tree) into every configured target language, and stores them
/// in the <see cref="LocalizedNavTitle"/> key/value table.
///
/// Document leaf titles are already localized through <see cref="LocalizeDocumentsJob"/> (via
/// <see cref="LocalizedDocument"/>); this job covers the remaining piece — the group/folder names
/// which are not backed by any <see cref="Document"/> row and therefore never entered that pipeline.
///
/// Design:
/// - Lazy: for every (label × culture) pair, if a row already exists it is skipped — no AI call.
///   The first run translates everything; subsequent runs are almost entirely cache hits.
/// - The <see cref="LocalizedNavTitle.SourceText"/> string is its own version: renaming a group in
///   properdocs.yml produces a new key that gets translated; the old row is simply left behind.
/// - Source-culture detection is itself lazy: it only runs when at least one pair is missing, and
///   feeds ALL labels to the AI once to determine the nav's original language. When a target culture
///   equals that source culture the label is passed through (stored as-is, no AI call).
/// - Translation is strictly one label at a time — labels are never batched into a single call.
/// </summary>
public class LocalizeNavTitlesJob(
    DocsViewerDbContext db,
    IHostEnvironment env,
    GlobalSettingsService settingsService,
    NavConfigParser navConfigParser,
    IDocumentTranslationService documentTranslationService,
    RetryEngine retryEngine,
    ChatClient chatClient,
    ILogger<LocalizeNavTitlesJob> logger) : IBackgroundJob
{
    public string Name => "Localize Nav Titles";

    public string Description =>
        "Translates the sidebar navigation group/folder labels from properdocs.yml into all " +
        "configured languages and stores them in the LocalizedNavTitles table. " +
        "Each (label × culture) pair already present in the table is skipped, so the AI is only " +
        "called for labels that are new or for newly-added target languages. Translation is done " +
        "one label at a time; labels are never combined into a single request.";

    public async Task ExecuteAsync()
    {
        if (!await settingsService.IsAiLocalizationEnabledAsync())
        {
            logger.LogInformation("LocalizeNavTitlesJob: AI endpoint not configured. Skipping.");
            return;
        }

        var languagesRaw = await settingsService.GetSettingValueAsync(SettingsMap.LocalizationLanguages);
        var cultures = languagesRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (cultures.Length == 0)
        {
            logger.LogInformation("LocalizeNavTitlesJob: No target languages configured. Skipping.");
            return;
        }

        var repoPath = Path.Combine(env.ContentRootPath, "App_Data", "DocsRepo");
        var navConfig = await navConfigParser.ParseAsync(repoPath);
        if (navConfig == null)
        {
            logger.LogInformation("LocalizeNavTitlesJob: no properdocs.yml nav config. Skipping.");
            return;
        }

        var titles = new List<string>();
        CollectGroupTitles(navConfig.NavItems, titles);
        titles = titles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (titles.Count == 0)
        {
            logger.LogInformation("LocalizeNavTitlesJob: nav has no group labels to translate. Skipping.");
            return;
        }

        // Lazily compute the missing (label × culture) pairs. Anything already stored is skipped.
        var existing = await db.LocalizedNavTitles
            .Where(nt => titles.Contains(nt.SourceText) && cultures.Contains(nt.Culture))
            .Select(nt => new { nt.SourceText, nt.Culture })
            .ToListAsync();
        var existingKeys = existing
            .Select(e => Key(e.SourceText, e.Culture))
            .ToHashSet();

        var missing = (from culture in cultures
                       from title in titles
                       where !existingKeys.Contains(Key(title, culture))
                       select (Title: title, Culture: culture)).ToList();

        if (missing.Count == 0)
        {
            logger.LogInformation("LocalizeNavTitlesJob: all {Count} nav labels up-to-date across {Cultures} language(s).",
                titles.Count, cultures.Length);
            return;
        }

        // Only now — because there is real work — spend one AI call to detect the nav source language.
        var sourceCulture = await DetectNavSourceCultureAsync(titles);
        logger.LogInformation("LocalizeNavTitlesJob: {Missing} pair(s) to process. Detected source culture: {Source}.",
            missing.Count, sourceCulture ?? "(unknown)");

        var processed = 0;
        foreach (var (title, culture) in missing)
        {
            try
            {
                string localized;
                if (sourceCulture != null && string.Equals(sourceCulture, culture, StringComparison.OrdinalIgnoreCase))
                {
                    // Pass-through: same language — store the original, no AI call.
                    localized = title;
                    logger.LogInformation("LocalizeNavTitlesJob: pass-through '{Title}' ({Culture}).", title, culture);
                }
                else
                {
                    // Translate this single label on its own — never batched with others.
                    localized = await documentTranslationService.TranslateAsync(title, culture);
                    logger.LogInformation("LocalizeNavTitlesJob: translated '{Title}' → '{Localized}' ({Culture}).",
                        title, localized, culture);
                }

                await UpsertAsync(title, culture, localized);
                await db.SaveChangesAsync();
                processed++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LocalizeNavTitlesJob: failed to localize '{Title}' to {Culture}.", title, culture);
            }
        }

        logger.LogInformation("LocalizeNavTitlesJob: done. Processed {Count} pair(s) this run.", processed);
    }

    private async Task UpsertAsync(string sourceText, string culture, string localizedText)
    {
        var existing = await db.LocalizedNavTitles
            .FirstOrDefaultAsync(nt => nt.SourceText == sourceText && nt.Culture == culture);

        if (existing == null)
        {
            db.LocalizedNavTitles.Add(new LocalizedNavTitle
            {
                SourceText = sourceText,
                Culture = culture,
                LocalizedText = localizedText,
                LastLocalizedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.LocalizedText = localizedText;
            existing.LastLocalizedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Collects the labels of branch nav entries (groups/folders) AND external links.
    /// These are exactly the labels the sidebar renders using LocalizedNavTitles.
    /// </summary>
    private static void CollectGroupTitles(List<NavEntry> entries, List<string> into)
    {
        foreach (var entry in entries)
        {
            if (entry.Children.Count > 0)
            {
                into.Add(entry.Title);
                CollectGroupTitles(entry.Children, into);
            }
            else if (!string.IsNullOrWhiteSpace(entry.Path) && (entry.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || entry.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                into.Add(entry.Title);
            }
        }
    }

    private static string Key(string sourceText, string culture) => $"{sourceText} {culture}";

    /// <summary>
    /// Detects the original language of the navigation by feeding ALL group labels to the AI at once
    /// and asking for a single BCP-47 code. Returns null if detection fails, in which case the caller
    /// translates every target culture (never passing through) — safe, if slightly wasteful.
    /// </summary>
    protected virtual async Task<string?> DetectNavSourceCultureAsync(List<string> titles)
    {
        try
        {
            var sample = string.Join("\n", titles);
            if (sample.Length > 1000) sample = sample[..1000];

            var endpoint = await settingsService.GetSettingValueAsync(SettingsMap.OpenAiInstance);
            var model = await settingsService.GetSettingValueAsync(SettingsMap.OpenAiLocalizationModel);
            var token = await settingsService.GetSettingValueAsync(SettingsMap.OpenAiApiToken);

            var result = await retryEngine.RunWithRetry(async _ =>
            {
                var response = await chatClient.AskModel(new OpenAiRequestModel
                {
                    Model = model,
                    Stream = false,
                    Messages =
                    [
                        new MessagesItem
                        {
                            Role = "user",
                            Content = "The following lines are navigation menu labels of a documentation site. " +
                                      "Detect their original language. Reply with ONLY a BCP-47 code like \"en-US\", " +
                                      "\"zh-CN\", \"ja-JP\", etc. No other text.\n\n" + sample
                        }
                    ]
                }, endpoint, token, CancellationToken.None);

                return response.GetAnswerPart().Trim();
            }, attempts: 3);

            if (string.IsNullOrWhiteSpace(result)) return null;
            result = result.Trim('"', '.', ' ', '\n', '\r');

            try
            {
                return CultureInfo.GetCultureInfo(result, predefinedOnly: true).Name;
            }
            catch (CultureNotFoundException)
            {
                logger.LogWarning("LocalizeNavTitlesJob: AI returned invalid culture '{Result}' for nav labels.", result);
                return null;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LocalizeNavTitlesJob: failed to detect nav source culture.");
            return null;
        }
    }
}
