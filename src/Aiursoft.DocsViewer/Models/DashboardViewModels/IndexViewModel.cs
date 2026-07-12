using Aiursoft.UiStack.Layout;

namespace Aiursoft.DocsViewer.Models.DashboardViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Dashboard";
    }

    public string DisplayName { get; set; } = string.Empty;
}
