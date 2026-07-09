using Aiursoft.DocsViewer.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DocsViewer.Models.FavoritesViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public List<DocumentFavorite> Favorites { get; set; } = [];
    public Dictionary<int, string> LocalizedTitles { get; set; } = [];
    public Dictionary<int, string> LocalizedContents { get; set; } = [];
}
