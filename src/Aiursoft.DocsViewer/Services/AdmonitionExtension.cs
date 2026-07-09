using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Aiursoft.DocsViewer.Services;

/// <summary>
/// A Markdig extension that parses MkDocs Material admonition syntax:
/// !!! type "Optional Title"
///     indented content lines
/// </summary>
public class AdmonitionExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        var blockParsers = pipeline.BlockParsers;
        if (!blockParsers.Contains<AdmonitionParser>())
        {
            blockParsers.Insert(0, new AdmonitionParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer htmlRenderer)
        {
            var objectRenderers = htmlRenderer.ObjectRenderers;
            if (!objectRenderers.Contains<AdmonitionRenderer>())
            {
                objectRenderers.Insert(0, new AdmonitionRenderer());
            }
        }
    }
}

public class AdmonitionParser : BlockParser
{
    public AdmonitionParser()
    {
        OpeningCharacters = ['!'];
    }

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent) return BlockState.None;

        var line = processor.Line;
        var slice = line.ToString();

        // Must start with "!!! " followed by a type word
        if (!slice.StartsWith("!!! ") && !slice.StartsWith("??? ")) return BlockState.None;

        var rest = slice[4..].Trim();
        if (rest.Length == 0) return BlockState.None;

        // Parse type (required) and optional title (in quotes)
        string type;
        string? title = null;

        var spaceIdx = rest.IndexOf(' ');
        if (spaceIdx > 0)
        {
            type = rest[..spaceIdx].ToLowerInvariant();
            var afterType = rest[(spaceIdx + 1)..].Trim();

            // Extract title from quotes
            if (afterType.Length >= 2 && afterType.StartsWith('\"'))
            {
                var endQuote = afterType.IndexOf('\"', 1);
                if (endQuote > 1)
                {
                    title = afterType[1..endQuote];
                }
            }
            else if (afterType.Length > 0)
            {
                title = afterType;
            }
        }
        else
        {
            type = rest.ToLowerInvariant();
        }

        var block = new AdmonitionBlock(this)
        {
            AdmonitionType = type,
            Title = title
        };

        processor.NewBlocks.Push(block);
        return BlockState.Continue;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var admonition = (AdmonitionBlock)block;

        if (processor.IsBlankLine)
        {
            // Blank line ends the admonition
            block.Update(processor);
            return BlockState.Break;
        }

        // Continue if indented (4 spaces) or if it's a continuation line
        if (processor.IsCodeIndent || processor.Column > 3)
        {
            block.Update(processor);
            return BlockState.Continue;
        }

        // Check if the next line starts a new admonition
        var slice = processor.Line.ToString();
        if (slice.StartsWith("!!! ") || slice.StartsWith("??? "))
        {
            block.Update(processor);
            return BlockState.Break;
        }

        block.Update(processor);
        return BlockState.Break;
    }

    public override bool Close(BlockProcessor processor, Block block)
    {
        block.Update(processor);
        return true;
    }
}

public class AdmonitionBlock : ContainerBlock
{
    public string AdmonitionType { get; set; } = "note";
    public string? Title { get; set; }

    public AdmonitionBlock(BlockParser parser) : base(parser) { }
}

public class AdmonitionRenderer : HtmlObjectRenderer<AdmonitionBlock>
{
    protected override void Write(HtmlRenderer renderer, AdmonitionBlock block)
    {
        var type = block.AdmonitionType;
        var title = block.Title ?? Capitalize(type);

        // Map type to a Bootstrap-friendly icon and color
        var (icon, bgClass, borderClass) = type switch
        {
            "note" or "info" => ("ℹ️", "bg-primary bg-opacity-10", "border-primary"),
            "abstract" or "summary" => ("📄", "bg-secondary bg-opacity-10", "border-secondary"),
            "tip" or "hint" => ("💡", "bg-success bg-opacity-10", "border-success"),
            "success" or "check" or "done" => ("✅", "bg-success bg-opacity-10", "border-success"),
            "question" or "help" or "faq" => ("❓", "bg-info bg-opacity-10", "border-info"),
            "warning" => ("⚠️", "bg-warning bg-opacity-10", "border-warning"),
            "failure" or "fail" => ("❌", "bg-danger bg-opacity-10", "border-danger"),
            "danger" or "error" => ("🚫", "bg-danger bg-opacity-10", "border-danger"),
            "bug" => ("🐛", "bg-danger bg-opacity-10", "border-danger"),
            "example" => ("📖", "bg-success bg-opacity-10", "border-success"),
            "quote" or "cite" => ("💬", "bg-secondary bg-opacity-10", "border-secondary"),
            _ => ("📝", "bg-light", "border-secondary")
        };

        var collapsible = block.Parser is AdmonitionParser p && p.OpeningCharacters is ['?'];

        renderer.Write($"<div class=\"admonition {bgClass} border-start border-4 {borderClass} rounded p-3 my-3\">");
        renderer.Write($"<p class=\"admonition-title fw-semibold mb-2\">{icon} {title}</p>");

        if (collapsible)
        {
            renderer.Write("<details open><summary class=\"d-none\"></summary>");
        }

        foreach (var child in block)
        {
            renderer.WriteChildren(child);
        }

        if (collapsible)
        {
            renderer.Write("</details>");
        }

        renderer.Write("</div>");
    }

    private static string Capitalize(string s) =>
        s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s;
}
