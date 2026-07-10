using Aiursoft.DocsViewer.Services;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public class AdmonitionPreprocessorTests
{
    // ──────────────────────────────────────────────────────
    // Happy path — all 5 types found in actual docs
    // ──────────────────────────────────────────────────────

    [TestMethod]
    public void Warning_WithTitle_RendersCorrectly()
    {
        var input = "!!! warning \"Security Risk\"\n    Disabling the password requirement for sudo can be a security risk.\n    This may cause some commands running without sudo to have root permissions.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("admonition"), "Should contain admonition div");
        Assert.IsTrue(result.Contains("bg-warning-subtle"), "Should have warning color");
        Assert.IsTrue(result.Contains("border-warning"), "Should have warning border");
        Assert.IsTrue(result.Contains("Security Risk"), "Should contain the title");
        Assert.IsTrue(result.Contains("Disabling the password"), "Should contain body text");
        Assert.IsFalse(result.Contains("!!! warning"), "Header syntax should be removed");
    }

    [TestMethod]
    public void Warning_WithoutTitle_RendersWithCapitalizedType()
    {
        var input = "!!! warning\n    Something important here.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("admonition"), "Should contain admonition div");
        Assert.IsTrue(result.Contains("Warning"), "Capitalized type should be title");
        Assert.IsFalse(result.Contains("!!! warning"), "Header syntax should be removed");
    }

    [TestMethod]
    public void Tip_RendersCorrectly()
    {
        var input = "!!! tip \"Quick Tip\"\n    Use `Ctrl+C` to copy text.\n    Works in most applications.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("bg-body-tertiary"), "Should have tip background");
        Assert.IsTrue(result.Contains("border-success"), "Should have success border");
        Assert.IsTrue(result.Contains("Quick Tip"), "Should contain the title");
        Assert.IsTrue(result.Contains("Ctrl+C"), "Should contain body inline code text");
    }

    [TestMethod]
    public void Note_RendersCorrectly()
    {
        var input = "!!! note\n    This is a simple note.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("admonition"), "Should contain admonition div");
        Assert.IsTrue(result.Contains("border-primary"), "Should have primary border");
        Assert.IsTrue(result.Contains("Note"), "Capitalized type as title");
        Assert.IsTrue(result.Contains("This is a simple note"), "Should contain body text");
    }

    [TestMethod]
    public void Info_RendersCorrectly()
    {
        var input = "!!! info \"Did you know?\"\n    The sky is blue because of Rayleigh scattering.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("admonition"), "Should contain admonition div");
        Assert.IsTrue(result.Contains("bg-body-tertiary"), "Should have info background");
        Assert.IsTrue(result.Contains("Did you know?"), "Should contain the title");
    }

    [TestMethod]
    public void Danger_RendersCorrectly()
    {
        var input = "!!! danger \"Critical Warning\"\n    This action cannot be undone.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("admonition"), "Should contain admonition div");
        Assert.IsTrue(result.Contains("bg-danger-subtle"), "Should have danger background");
        Assert.IsTrue(result.Contains("border-danger"), "Should have danger border");
        Assert.IsTrue(result.Contains("Critical Warning"), "Should contain the title");
    }

    // ──────────────────────────────────────────────────────
    // Collapsible variants
    // ──────────────────────────────────────────────────────

    [TestMethod]
    public void Collapsible_Tip_RendersCorrectly()
    {
        var input = "??? tip \"Expand for details\"\n    Hidden content here.\n    More hidden content.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("admonition"), "Should contain admonition div");
        Assert.IsTrue(result.Contains("<details open>"), "Should be collapsible");
        Assert.IsTrue(result.Contains("Expand for details"), "Should contain title");
        Assert.IsTrue(result.Contains("Hidden content here"), "Should contain body");
        Assert.IsFalse(result.Contains("??? tip"), "Header syntax should be removed");
    }

    [TestMethod]
    public void Collapsible_Note_RendersCorrectly()
    {
        var input = "??? note\n    Collapsible note body.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("<details open>"), "Should have details tag");
        Assert.IsTrue(result.Contains("</details>"), "Should close details tag");
    }

    // ──────────────────────────────────────────────────────
    // Edge cases
    // ──────────────────────────────────────────────────────

    [TestMethod]
    public void EmptyBody_RendersCorrectly()
    {
        var input = "!!! tip \"Empty\"\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        // An admonition with no body still renders as an admonition div with just the title
        Assert.IsTrue(result.Contains("admonition"), "Should contain admonition div");
        Assert.IsTrue(result.Contains("Empty"), "Should contain the title");
        Assert.IsFalse(result.Contains("!!! tip"), "Header syntax should be removed");
    }

    [TestMethod]
    public void MultipleAdmonitions_InOneDocument_AllProcessed()
    {
        var input = "Some text before.\n\n!!! warning \"First\"\n    Body one.\n\nMore text.\n\n!!! tip \"Second\"\n    Body two.\n\nAfter text.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsFalse(result.Contains("!!! warning"), "First header removed");
        Assert.IsFalse(result.Contains("!!! tip"), "Second header removed");
        Assert.IsTrue(result.Contains("admonition"), "Should contain admonition divs");
        Assert.IsTrue(result.Contains("First"), "Should contain first title");
        Assert.IsTrue(result.Contains("Second"), "Should contain second title");
        Assert.IsTrue(result.Contains("Some text before"), "Non-admonition text preserved");
        Assert.IsTrue(result.Contains("More text"), "Text between admonitions preserved");
        Assert.IsTrue(result.Contains("After text"), "Text after admonitions preserved");
    }

    [TestMethod]
    public void ContinuousAdmonitions_BackToBack()
    {
        var input = "!!! warning \"A\"\n    Body A.\n!!! danger \"B\"\n    Body B.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsFalse(result.Contains("!!! warning"), "First header removed");
        Assert.IsFalse(result.Contains("!!! danger"), "Second header removed");
        Assert.IsTrue(result.Contains("Body A"), "First body present");
        Assert.IsTrue(result.Contains("Body B"), "Second body present");
    }

    [TestMethod]
    public void DosLineEndings_HandledCorrectly()
    {
        var input = "!!! note \"Title\"\r\n    Body line one.\r\n    Body line two.\r\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("admonition"));
        Assert.IsFalse(result.Contains("!!! note"));
    }

    [TestMethod]
    public void NotExclamation_FalsePositive_Preserved()
    {
        // !! (two exclamation marks) should NOT be processed
        var input = "!! This is just a comment\n    not an admonition.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.AreEqual(input, result, "!! should not be treated as admonition");
    }

    [TestMethod]
    public void NotExclamation_ImageSyntax_Preserved()
    {
        // ![...](...) should NOT be processed
        var input = "![A cute cat](./images/cat.jpg)\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.AreEqual(input, result, "Image syntax should not be treated as admonition");
    }

    [TestMethod]
    public void UnknownType_UsesDefaultStyle()
    {
        var input = "!!! customtype \"Something\"\n    Body text.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("admonition"), "Should still produce admonition div");
        Assert.IsTrue(result.Contains("Something"), "Should contain title");
        Assert.IsTrue(result.Contains("Body text"), "Should contain body");
        Assert.IsFalse(result.Contains("!!! customtype"), "Header removed");
    }

    [TestMethod]
    public void Malformed_NoBody_PassThrough()
    {
        // No indented body — admonition renders empty, non-indented text passes through
        var input = "!!! warning \"No body here\"\nJust regular text.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("admonition"), "Should render admonition div even without indented body");
        Assert.IsTrue(result.Contains("No body here"), "Should contain title");
        Assert.IsTrue(result.Contains("Just regular text"), "Non-indented text should pass through unchanged");
        Assert.IsFalse(result.Contains("!!! warning"), "Header syntax should be removed");
    }

    [TestMethod]
    public void IndentedContent_AggregatedCorrectly()
    {
        var input = "!!! info \"Multi-line\"\n    First line.\n    Second line.\n\n    Third after blank.\n";
        var result = AdmonitionPreprocessor.Preprocess(input);

        Assert.IsTrue(result.Contains("First line"));
        Assert.IsTrue(result.Contains("Second line"));
        Assert.IsTrue(result.Contains("Third after blank"));
        Assert.IsTrue(result.Contains("</div>"), "Should close the div");
    }
}
