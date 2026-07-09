using System.Text.RegularExpressions;

namespace Aiursoft.DocsViewer.Services;

/// <summary>
/// Pre-processes markdown to convert MkDocs Material admonition syntax into HTML
/// that can then be passed through Markdig for further rendering of nested content.
/// </summary>
public static partial class AdmonitionPreprocessor
{
    // Matches:
    //   !!! type "Optional Title"
    //       indented body lines
    // or:
    //   ??? type "Optional Title"
    //       indented body lines
    [GeneratedRegex(
        @"^(?<prefix>!{3}|\?{3})\s+(?<type>\w+)(?:\s+""(?<title>[^""]*)""\s*)?\r?\n" +
        @"((?<bodyline>\ {4}.*(?:\r?\n|$))+)",
        RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex AdmonitionRegex();

    public static string Preprocess(string markdown)
    {
        return AdmonitionRegex().Replace(markdown, match =>
        {
            var isCollapsible = match.Groups["prefix"].Value == "???";
            var type = match.Groups["type"].Value.ToLowerInvariant();
            var title = match.Groups["title"].Success ? match.Groups["title"].Value : null;
            var bodyLines = match.Groups["bodyline"].Captures
                .Select(c => c.Value.TrimStart()) // strip leading 4 spaces
                .ToList();
            var body = string.Join("", bodyLines);

            title ??= char.ToUpper(type[0]) + type[1..];

            var (emoji, bgClass, borderClass) = type switch
            {
                "note" or "info" => ("&#8505;&#65039;", "bg-body-tertiary", "border-primary"),
                "abstract" or "summary" => ("&#128196;", "bg-body-tertiary", "border-secondary"),
                "tip" or "hint" => ("&#128161;", "bg-body-tertiary", "border-success"),
                "success" or "check" or "done" => ("&#9989;", "bg-body-tertiary", "border-success"),
                "question" or "help" or "faq" => ("&#10067;", "bg-body-tertiary", "border-info"),
                "warning" => ("&#9888;&#65039;", "bg-warning-subtle", "border-warning"),
                "failure" or "fail" => ("&#10060;", "bg-danger-subtle", "border-danger"),
                "danger" or "error" => ("&#128721;", "bg-danger-subtle", "border-danger"),
                "bug" => ("&#128027;", "bg-danger-subtle", "border-danger"),
                "example" => ("&#128214;", "bg-body-tertiary", "border-success"),
                "quote" or "cite" => ("&#128172;", "bg-body-tertiary", "border-secondary"),
                _ => ("&#128221;", "bg-body-tertiary", "border-secondary")
            };

            return
                $"<div class=\"admonition {bgClass} border-start border-4 {borderClass} rounded-2 p-3 my-3\">\n" +
                $"<p class=\"admonition-title fw-semibold mb-2\">{emoji} {title}</p>\n" +
                (isCollapsible ? "<details open><summary class=\"d-none\"></summary>\n" : "") +
                $"\n{body}\n\n" +
                (isCollapsible ? "</details>\n" : "") +
                $"</div>\n";
        });
    }
}
