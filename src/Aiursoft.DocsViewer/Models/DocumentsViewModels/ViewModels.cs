using Aiursoft.DocsViewer.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DocsViewer.Models.DocumentsViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public const int PageSize = 12;
    public string? Category { get; set; }
    public string? SortBy { get; set; }
    public string? CategoryDisplayName { get; set; }
    public List<Document> Documents { get; set; } = [];
    public int TotalCount { get; set; }
    public bool HasMore { get; set; }
    public Dictionary<int, int> LikeCounts { get; set; } = [];
    public Dictionary<int, string> LocalizedTitles { get; set; } = [];
    public Dictionary<int, string> LocalizedContents { get; set; } = [];
}

public class DocumentCardsViewModel
{
    public List<Document> Documents { get; set; } = [];
    public Dictionary<int, int> LikeCounts { get; set; } = [];
    public Dictionary<int, string> LocalizedTitles { get; set; } = [];
    public Dictionary<int, string> LocalizedContents { get; set; } = [];
}

public class DetailViewModel : UiStackLayoutViewModel
{
    public Document Document { get; set; } = null!;
    public string RenderedMarkdown { get; set; } = string.Empty;
    public bool IsFavorited { get; set; }
    public bool IsLiked { get; set; }
    public int LikeCount { get; set; }
    public List<DocumentComment> Comments { get; set; } = [];
    public string? GitHubEditUrl { get; set; }
    public string? GitHubHistoryUrl { get; set; }
    public string? CategoryDisplayName { get; set; }
    public LocalizedDocument? LocalizedDocument { get; set; }
    public bool ShowSimilarDocumentsButton { get; set; }

    /// <summary>
    /// True when the current UI culture differs from the document's SourceCulture,
    /// meaning the user is reading a machine translation.
    /// </summary>
    public bool ShowTranslationNotice { get; set; }

    /// <summary>
    /// Human-readable name of the document's source language, e.g. "中文 (中国大陆)".
    /// Populated via LanguageMetadata.SupportedCultures.
    /// </summary>
    public string? SourceLanguageName { get; set; }
}

public class SimilarViewModel : UiStackLayoutViewModel
{
    public Document SourceDocument { get; set; } = null!;
    public string? CategoryDisplayName { get; set; }
    public List<Document> SimilarDocuments { get; set; } = [];
    public Dictionary<int, int> LikeCounts { get; set; } = [];
    public Dictionary<int, string> LocalizedTitles { get; set; } = [];
    public Dictionary<int, string> LocalizedContents { get; set; } = [];
}

public class SearchViewModel : UiStackLayoutViewModel
{
    public const int PageSize = 10;
    public string? Keyword { get; set; }
    public int Page { get; set; }
    public int TotalCount { get; set; }
    public List<Document> Documents { get; set; } = [];
    public bool UsedAi { get; set; }
    public bool RateLimited { get; set; }
    public Dictionary<int, int> LikeCounts { get; set; } = [];
    public Dictionary<int, string> LocalizedTitles { get; set; } = [];
    public Dictionary<int, string> LocalizedContents { get; set; } = [];
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}
