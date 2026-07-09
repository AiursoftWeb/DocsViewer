using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Models.FavoritesViewModels;
using Aiursoft.DocsViewer.Services;
using Aiursoft.UiStack.Navigation;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.DocsViewer.Controllers;

[Authorize]
[LimitPerMin]
public class FavoritesController(
    DocsViewerDbContext db,
    UserManager<User> userManager,
    DocumentLocalizationService documentLocalization) : Controller
{
    [RenderInNavBar(
        NavGroupName = "Settings",
        NavGroupOrder = 9998,
        CascadedLinksGroupName = "Personal",
        CascadedLinksIcon = "user-circle",
        CascadedLinksOrder = 1,
        LinkText = "My Favorites",
        LinkOrder = 2)]
    [HttpGet]
    [ExcludeFromCodeCoverage]
    public async Task<IActionResult> Index()
    {
        var userId = userManager.GetUserId(User)!;
        var favorites = await db.DocumentFavorites
            .Where(f => f.UserId == userId)
            .Include(f => f.Document)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync();

        var documents = favorites.Select(f => f.Document).ToList();
        var (localizedTitles, localizedContents) = await documentLocalization.LoadLocalizedStringsAsync(documents);

        return this.StackView(new IndexViewModel
        {
            Favorites = favorites,
            LocalizedTitles = localizedTitles,
            LocalizedContents = localizedContents
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int documentId)
    {
        var userId = userManager.GetUserId(User)!;
        var existing = await db.DocumentFavorites
            .FirstOrDefaultAsync(f => f.UserId == userId && f.DocumentId == documentId);

        if (existing == null)
            db.DocumentFavorites.Add(new DocumentFavorite { UserId = userId, DocumentId = documentId });
        else
            db.DocumentFavorites.Remove(existing);

        await db.SaveChangesAsync();
        return RedirectToAction("DetailById", "Documents", new { id = documentId });
    }
}
