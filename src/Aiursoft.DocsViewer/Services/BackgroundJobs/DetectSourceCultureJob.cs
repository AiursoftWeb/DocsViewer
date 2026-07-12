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
/// Detects the original (source) language of each document from its content
/// using the configured AI Chat Endpoint. Only processes documents whose
/// <see cref="Document.SourceCulture"/> is null.
///
/// Downstream jobs (localization) skip documents with null SourceCulture —
/// this job must run first to unlock them.
///
/// Behavior per document state:
/// - SourceCulture is null  → Detect: AI analyzes content and sets SourceCulture
/// - SourceCulture is set   → Skip (already classified)
/// </summary>
public class DetectSourceCultureJob(
    DocsViewerDbContext db,
    GlobalSettingsService settingsService,
    RetryEngine retryEngine,
    ChatClient chatClient,
    ILogger<DetectSourceCultureJob> logger) : IBackgroundJob
{
    private const int ContentSampleLength = 500;

    public string Name => "Detect Source Culture";

    public string Description =>
        "Detects the original (source) language of each document from its content using AI. " +
        "Only processes documents whose SourceCulture is null. " +
        "Downstream localization jobs skip documents with null SourceCulture — " +
        "this job must run first to unlock them.";

    public async Task ExecuteAsync()
    {
        if (!await settingsService.IsAiLocalizationEnabledAsync())
        {
            logger.LogInformation("DetectSourceCultureJob: AI endpoint not configured. Skipping.");
            return;
        }

        var lastId = 0;
        var total = 0;

        while (true)
        {
            var currentLastId = lastId;

            var pending = await db.Documents
                .Where(d => d.SourceCulture == null &&
                            !d.IsDeleted &&
                            d.Id > currentLastId)
                .OrderBy(d => d.Id)
                .Take(20)
                .ToListAsync();

            if (pending.Count == 0) break;

            foreach (var doc in pending)
            {
                var detected = await DetectCultureAsync(doc);
                if (detected != null)
                {
                    doc.SourceCulture = detected;
                    total++;
                    await db.SaveChangesAsync();
                }
            }

            lastId = pending.Max(d => d.Id);
        }

        logger.LogInformation("DetectSourceCultureJob: done. Detected {Count} language(s).", total);
    }

    private static bool IsValidCulture(string result, out string normalized)
    {
        try
        {
            var ci = CultureInfo.GetCultureInfo(result, predefinedOnly: true);
            normalized = ci.Name;
            return true;
        }
        catch (CultureNotFoundException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    protected virtual async Task<string?> DetectCultureAsync(Document doc)
    {
        try
        {
            var text = doc.Content.Length > 0 ? doc.Content : doc.Title;
            if (text.Length > ContentSampleLength)
                text = text[..ContentSampleLength];

            if (string.IsNullOrWhiteSpace(text)) return null;

            var endpoint = await settingsService.GetSettingValueAsync(SettingsMap.OpenAiInstance);
            var model    = await settingsService.GetSettingValueAsync(SettingsMap.OpenAiLocalizationModel);
            var token    = await settingsService.GetSettingValueAsync(SettingsMap.OpenAiApiToken);

            var result = await retryEngine.RunWithRetry(async _ =>
            {
                var response = await chatClient.AskModel(new OpenAiRequestModel
                {
                    Model  = model,
                    Stream = false,
                    Messages =
                    [
                        new MessagesItem
                        {
                            Role    = "user",
                            Content = $"Detect the language of this text. Reply with ONLY a BCP-47 code like \"en-US\", \"zh-CN\", \"ja-JP\", etc. No other text.\n\n{text}"
                        }
                    ]
                }, endpoint, token, CancellationToken.None);

                return response.GetAnswerPart().Trim();
            }, attempts: 3);

            if (string.IsNullOrWhiteSpace(result)) return null;

            // Normalize: strip quotes, dots, extra whitespace
            result = result.Trim('"', '.', ' ', '\n', '\r');

            if (IsValidCulture(result, out var normalized))
            {
                logger.LogInformation(
                    "DetectSourceCultureJob: '{Title}' → {Culture}.", doc.Title, normalized);
                return normalized;
            }

            logger.LogWarning(
                "DetectSourceCultureJob: AI returned invalid culture '{Result}' for '{Title}'.",
                result, doc.Title);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "DetectSourceCultureJob: failed to detect culture for '{Title}'.", doc.Title);
            return null;
        }
    }
}
