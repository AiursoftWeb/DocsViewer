using Aiursoft.UiStack.Layout;

namespace Aiursoft.DocsViewer.Models.HomeViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public bool HasReadme { get; set; }
    public string ReadmeHtml { get; set; } = string.Empty;
    public int? DocumentId { get; set; }
}
