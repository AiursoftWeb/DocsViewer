using Aiursoft.DocsViewer.Entities;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Controllers;

[Authorize]
[LimitPerMin]
public class LikesController(
    DocsViewerDbContext db,
    UserManager<User> userManager) : Controller
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int documentId)
    {
        var documentExists = await db.Documents.AnyAsync(r => r.Id == documentId);
        if (!documentExists)
            return NotFound();

        var userId = userManager.GetUserId(User)!;
        var existing = await db.DocumentLikes
            .FirstOrDefaultAsync(l => l.UserId == userId && l.DocumentId == documentId);

        if (existing != null)
            db.DocumentLikes.Remove(existing);
        else
            db.DocumentLikes.Add(new DocumentLike { UserId = userId, DocumentId = documentId });

        await db.SaveChangesAsync();
        return RedirectToAction("DetailById", "Documents", new { id = documentId });
    }
}
