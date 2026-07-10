using System.Text.RegularExpressions;

namespace Aiursoft.DocsViewer.Services;

/// <summary>
/// Pre-processes markdown to convert MkDocs Material admonition syntax into HTML
/// that can then be passed through Markdig for further rendering of nested content.
/// Uses a line-by-line parser to correctly handle blank lines inside admonition bodies.
/// </summary>
public static partial class AdmonitionPreprocessor
{
    // Matches the opening line:  !!! type "Optional Title"  or  ??? type "Optional Title"
    [GeneratedRegex(@"^(?<prefix>!{3}|\?{3})\s+(?<type>\w+)(?:\s+""(?<title>[^""]*)""\s*)?$", RegexOptions.Compiled)]
    private static partial Regex AdmonitionOpeningRegex();

    public static string Preprocess(string markdown)
    {
        var lines = markdown.Split('\n');
        var result = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = AdmonitionOpeningRegex().Match(line);

            if (!match.Success)
            {
                result.Add(line);
                continue;
            }

            // Found an admonition opening — collect all indented body lines
            var isCollapsible = match.Groups["prefix"].Value == "???";
            var type = match.Groups["type"].Value.ToLowerInvariant();
            var title = match.Groups["title"].Success ? match.Groups["title"].Value : null;
            title ??= char.ToUpper(type[0]) + type[1..];

            i++; // move past the opening line
            var bodyLines = new List<string>();
            var foundAnyContent = false;

            while (i < lines.Length)
            {
                var bodyLine = lines[i];

                if (string.IsNullOrWhiteSpace(bodyLine))
                {
                    // Blank line: include it if we've already started collecting content
                    if (foundAnyContent)
                    {
                        bodyLines.Add("");
                    }
                    else
                    {
                        // Blank line before any content — skip it
                    }
                    i++;
                }
                else if (bodyLine.StartsWith("    "))
                {
                    // Indented body line — strip exactly 4 leading spaces
                    foundAnyContent = true;
                    bodyLines.Add(bodyLine[4..]);
                    i++;
                }
                else if (bodyLine.StartsWith('\t'))
                {
                    // Tab-indented body line — strip the tab
                    foundAnyContent = true;
                    bodyLines.Add(bodyLine[1..]);
                    i++;
                }
                else
                {
                    // Non-indented, non-blank line — end of admonition
                    break;
                }
            }

            // Step back so the outer loop picks up the terminating line
            i--;

            // Trim trailing blank lines from the body
            while (bodyLines.Count > 0 && string.IsNullOrEmpty(bodyLines[^1]))
                bodyLines.RemoveAt(bodyLines.Count - 1);

            var body = string.Join("\n", bodyLines);

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

            var html = $"<div class=\"admonition {bgClass} border-start border-4 {borderClass} rounded-2 p-3 my-3\">\n" +
                       $"<p class=\"admonition-title fw-semibold mb-2\">{emoji} {title}</p>\n" +
                       (isCollapsible ? "<details open><summary class=\"d-none\"></summary>\n" : string.Empty) +
                       $"\n{body}\n\n" +
                       (isCollapsible ? "</details>\n" : string.Empty) +
                       "</div>\n";

            result.Add(html);
        }

        return string.Join("\n", result);
    }
}
