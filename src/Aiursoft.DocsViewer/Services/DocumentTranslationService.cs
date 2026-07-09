using Aiursoft.Canon;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.Dotlang.Shared;
using Aiursoft.GptClient.Services;
using Aiursoft.Scanner.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.DocsViewer.Services;

/// <summary>
/// Abstraction for translating document text into target languages via an AI endpoint.
/// </summary>
public interface IDocumentTranslationService
{
    Task<string> TranslateAsync(string text, string targetLanguage);
}

/// <summary>
/// Translates text using Dotlang's <see cref="OllamaBasedTranslatorEngine"/>.
/// AI settings (instance, model, token) are read from <see cref="GlobalSettingsService"/>
/// at call time so admin changes take effect immediately.
/// </summary>
[ExcludeFromCodeCoverage]
public class DocumentTranslationService(
    GlobalSettingsService settingsService,
    MarkdownShredder shredder,
    RetryEngine retryEngine,
    ILogger<OllamaBasedTranslatorEngine> engineLogger,
    ChatClient chatClient) : IScopedDependency, IDocumentTranslationService
{
    public async Task<string> TranslateAsync(string text, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var options = Options.Create(new TranslateOptions
        {
            OllamaInstance = await settingsService.GetSettingValueAsync(SettingsMap.OpenAiInstance),
            OllamaModel = await settingsService.GetSettingValueAsync(SettingsMap.OpenAiLocalizationModel),
            OllamaToken = await settingsService.GetSettingValueAsync(SettingsMap.OpenAiApiToken)
        });

        var engine = new OllamaBasedTranslatorEngine(options, retryEngine, engineLogger, chatClient, shredder);
        return await engine.TranslateAsync(text, targetLanguage);
    }
}
