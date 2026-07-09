using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Services;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Controllers;

[Authorize]
[LimitPerMin]
public class CommentsController(
    DocsViewerDbContext db,
    UserManager<User> userManager,
    GlobalSettingsService globalSettingsService) : Controller
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Post(int documentId, int? parentCommentId, string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 1000)
            return BadRequest();

        var documentExists = await db.Documents.AnyAsync(r => r.Id == documentId);
        if (!documentExists)
            return NotFound();

        if (parentCommentId.HasValue)
        {
            var parent = await db.DocumentComments
                .FirstOrDefaultAsync(c => c.Id == parentCommentId.Value && c.DocumentId == documentId);
            if (parent == null || parent.ParentCommentId != null) // max 2 levels
                return BadRequest();
        }

        var userId = userManager.GetUserId(User)!;

        // Rate limiting
        var maxCommentsPerDay = await globalSettingsService.GetIntSettingAsync(SettingsMap.MaxCommentsPerDayPerUser);
        var today = DateTime.UtcNow.Date;
        var commentCountToday = await db.DocumentComments
            .CountAsync(c => c.UserId == userId && c.CreatedAt >= today);

        if (commentCountToday >= maxCommentsPerDay)
        {
            return StatusCode(429);
        }

        db.DocumentComments.Add(new DocumentComment
        {
            DocumentId = documentId,
            UserId = userId,
            ParentCommentId = parentCommentId,
            Content = content.Trim(),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return RedirectToAction("DetailById", "Documents", new { id = documentId }, "comments");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int commentId)
    {
        var comment = await db.DocumentComments
            .Include(c => c.Replies)
            .FirstOrDefaultAsync(c => c.Id == commentId);

        if (comment == null)
            return NotFound();

        var userId = userManager.GetUserId(User);
        if (comment.UserId != userId)
            return Forbid();

        // Delete replies first, then the comment itself
        db.DocumentComments.RemoveRange(comment.Replies);
        db.DocumentComments.Remove(comment);
        await db.SaveChangesAsync();

        return RedirectToAction("DetailById", "Documents", new { id = comment.DocumentId }, "comments");
    }
}
