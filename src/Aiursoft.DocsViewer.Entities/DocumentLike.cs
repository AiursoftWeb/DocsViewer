using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Aiursoft.DocsViewer.Entities;

public class DocumentLike
{
    [MaxLength(450)]
    public required string UserId { get; set; }

    public int DocumentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    [ForeignKey(nameof(DocumentId))]
    public Document Document { get; set; } = null!;
}
