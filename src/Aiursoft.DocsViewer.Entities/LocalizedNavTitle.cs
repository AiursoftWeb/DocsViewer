using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.DocsViewer.Entities;

/// <summary>
/// A single AI-translated sidebar navigation group/folder label.
///
/// Unlike <see cref="LocalizedDocument"/>, navigation group names are not tied to a
/// <see cref="Document"/> row — they are arbitrary author-defined strings that come from
/// the <c>nav:</c> tree of the repo's <c>properdocs.yml</c>. This table is therefore a
/// simple key/value map: the key is <c>(SourceText, Culture)</c> and the value is
/// <see cref="LocalizedText"/>.
///
/// The <see cref="SourceText"/> string itself acts as the version: when an author renames a
/// nav group in properdocs.yml the new label is a brand-new key that gets translated, while
/// the old row simply becomes orphaned (eligible for later cleanup). This is why there is no
/// FileLastModified-style staleness tracking here.
/// </summary>
[ExcludeFromCodeCoverage]
public class LocalizedNavTitle
{
    [Key]
    public int Id { get; set; }

    /// <summary>The original navigation label as written in properdocs.yml, e.g. "Getting Started".</summary>
    [MaxLength(200)]
    public required string SourceText { get; set; }

    /// <summary>BCP-47 target culture tag, e.g. "en-US", "ja-JP", "zh-CN".</summary>
    [MaxLength(20)]
    public required string Culture { get; set; }

    /// <summary>The translated label for <see cref="Culture"/> (equal to <see cref="SourceText"/> for pass-through).</summary>
    [MaxLength(200)]
    public string LocalizedText { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the last successful AI translation (for observability / cleanup).</summary>
    public DateTime LastLocalizedAt { get; set; } = DateTime.UtcNow;
}
