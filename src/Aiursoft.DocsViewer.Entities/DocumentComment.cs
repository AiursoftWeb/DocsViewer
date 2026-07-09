using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Aiursoft.DocsViewer.Entities;

public class DocumentComment
{
    public int Id { get; set; }

    public int DocumentId { get; set; }

    [MaxLength(450)]
    public required string UserId { get; set; }

    /// <summary>Null = root comment on document. Non-null = reply to a root comment.</summary>
    public int? ParentCommentId { get; set; }

    [MaxLength(1000)]
    public required string Content { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(DocumentId))]
    public Document Document { get; set; } = null!;

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    [ForeignKey(nameof(ParentCommentId))]
    public DocumentComment? ParentComment { get; set; }

    public List<DocumentComment> Replies { get; set; } = [];
}
