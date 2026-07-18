namespace Aiursoft.DocsViewer.Models.DocumentsViewModels;

public class ContributorViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public int CommitCount { get; set; }
    public string GitHubProfileUrl { get; set; } = string.Empty;
}
