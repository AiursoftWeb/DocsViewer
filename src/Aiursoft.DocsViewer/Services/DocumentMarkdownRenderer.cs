using System.Text.RegularExpressions;
using Aiursoft.Scanner.Abstractions;
using Aiursoft.DocsViewer.Services.FileStorage;
using Markdig;

namespace Aiursoft.DocsViewer.Services;

/// <summary>
/// Renders markdown to HTML, resolving StorageService logical image paths to public URLs.
/// </summary>
public partial class DocumentMarkdownRenderer(StorageService storageService) : IScopedDependency
{
    /// <summary>
    /// Matches src attribute pointing to a logical workspace path (e.g. doc-images/abc123.png).
    /// </summary>
    [GeneratedRegex(@"src=""([^""]*doc-images/[^""]+)""", RegexOptions.Compiled)]
    private static partial Regex LogicalPathRegex();

    /// <summary>
    /// Matches img tags that lack a class attribute, to add Bootstrap's img-fluid.
    /// </summary>
    [GeneratedRegex(@"<img\s+(?![^>]*\bclass\s*=)", RegexOptions.Compiled)]
    private static partial Regex ImgWithoutClassRegex();

    public string RenderHtml(string markdown)
    {
        // Pre-process MkDocs admonitions before passing to Markdig
        markdown = AdmonitionPreprocessor.Preprocess(markdown);

        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseAutoLinks()
            .UsePipeTables()
            .UseGridTables()
            .Build();

        var html = Markdown.ToHtml(markdown, pipeline);

        // Replace logical storage paths with public URLs
        html = LogicalPathRegex().Replace(html, match =>
        {
            var logicalPath = match.Groups[1].Value;
            var url = storageService.RelativePathToInternetUrl(logicalPath, isVault: false);
            return $"src=\"{url}\"";
        });

        // Add Bootstrap img-fluid class to all images for responsive sizing
        html = ImgWithoutClassRegex().Replace(html, "<img class=\"img-fluid\" ");

        return html;
    }
}
