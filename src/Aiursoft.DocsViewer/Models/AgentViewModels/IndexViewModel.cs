using System.ComponentModel.DataAnnotations;
using Aiursoft.DocsViewer.Services.Agents;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DocsViewer.Models.AgentViewModels;

public sealed class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel() => PageTitle = "Document Assistant";

    [Required]
    [StringLength(2000)]
    public string Question { get; set; } = string.Empty;
    public bool Configured { get; set; }
    public string? Answer { get; set; }
    public string? StatusMessage { get; set; }
    public IReadOnlyList<DocumentCitation> Citations { get; set; } = [];
}
