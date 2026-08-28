using Aiursoft.DocsViewer.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DocsViewer.Models.CommentManagementViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public const int PageSize = 20;

    public List<CommentItemViewModel> Comments { get; set; } = [];
    public CommentStatus? Status { get; set; }
    public string? Keyword { get; set; }
    public string? Type { get; set; }
    public int Page { get; set; } = 1;
    public int TotalCount { get; set; }
    public int PendingCount { get; set; }
    public int ApprovedCount { get; set; }
    public int RejectedCount { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public class CommentItemViewModel
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public required string DocumentTitle { get; set; }
    public required string DocumentFilePath { get; set; }
    public required string UserId { get; set; }
    public required string UserName { get; set; }
    public required string DisplayName { get; set; }
    public int? ParentCommentId { get; set; }
    public string? ParentContent { get; set; }
    public required string Content { get; set; }
    public DateTime CreatedAt { get; set; }
    public CommentStatus Status { get; set; }
    public int RepliesCount { get; set; }

    public string DocumentUrl
    {
        get
        {
            var path = DocumentFilePath.Replace('\\', '/');
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^3];
            }

            return $"/{path}.html#comment-{Id}";
        }
    }
}
