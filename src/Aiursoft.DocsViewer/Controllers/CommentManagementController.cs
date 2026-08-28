using Aiursoft.DocsViewer.Authorization;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Models.CommentManagementViewModels;
using Aiursoft.DocsViewer.Services;
using Aiursoft.UiStack.Navigation;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Controllers;

[Authorize(Policy = AppPermissionNames.CanReadComments)]
[LimitPerMin]
public class CommentManagementController(
    DocsViewerDbContext db,
    UserManager<User> userManager) : Controller
{
    [HttpGet]
    [RenderInNavBar(
        NavGroupName = "Administration",
        NavGroupOrder = 9999,
        CascadedLinksGroupName = "Content",
        CascadedLinksIcon = "message-square",
        CascadedLinksOrder = 9997,
        LinkText = "Comments",
        LinkOrder = 1)]
    public async Task<IActionResult> Index(
        CommentStatus? status,
        string? keyword,
        string? type,
        int page = 1)
    {
        page = Math.Max(1, page);
        keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        type = type is "root" or "reply" ? type : null;

        var allComments = db.DocumentComments.AsNoTracking();
        var pendingCount = await allComments.CountAsync(c => c.Status == CommentStatus.Pending);
        var approvedCount = await allComments.CountAsync(c => c.Status == CommentStatus.Approved);
        var rejectedCount = await allComments.CountAsync(c => c.Status == CommentStatus.Rejected);

        var query = allComments.AsQueryable();
        if (status.HasValue)
        {
            query = query.Where(c => c.Status == status.Value);
        }

        if (type == "root")
        {
            query = query.Where(c => c.ParentCommentId == null);
        }
        else if (type == "reply")
        {
            query = query.Where(c => c.ParentCommentId != null);
        }

        if (keyword != null)
        {
            query = query.Where(c =>
                c.Content.Contains(keyword) ||
                c.User.UserName!.Contains(keyword) ||
                c.User.DisplayName.Contains(keyword) ||
                c.Document.Title.Contains(keyword));
        }

        var totalCount = await query.CountAsync();
        var comments = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * IndexViewModel.PageSize)
            .Take(IndexViewModel.PageSize)
            .Select(c => new CommentItemViewModel
            {
                Id = c.Id,
                DocumentId = c.DocumentId,
                DocumentTitle = c.Document.Title,
                DocumentFilePath = c.Document.FilePath,
                UserId = c.UserId,
                UserName = c.User.UserName!,
                DisplayName = c.User.DisplayName,
                ParentCommentId = c.ParentCommentId,
                ParentContent = c.ParentComment == null ? null : c.ParentComment.Content,
                Content = c.Content,
                CreatedAt = c.CreatedAt,
                Status = c.Status,
                RepliesCount = c.Replies.Count
            })
            .ToListAsync();

        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)IndexViewModel.PageSize));
        page = Math.Min(page, totalPages);
        if (page > 1 && comments.Count == 0)
        {
            return RedirectToAction(nameof(Index), new { status, keyword, type, page = totalPages });
        }

        return this.StackView(new IndexViewModel
        {
            PageTitle = "Comment Management",
            Comments = comments,
            Status = status,
            Keyword = keyword,
            Type = type,
            Page = page,
            TotalCount = totalCount,
            PendingCount = pendingCount,
            ApprovedCount = approvedCount,
            RejectedCount = rejectedCount
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanModerateComments)]
    public Task<IActionResult> Approve(int[] commentIds) =>
        SetStatus(commentIds, CommentStatus.Approved);

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanModerateComments)]
    public Task<IActionResult> Reject(int[] commentIds) =>
        SetStatus(commentIds, CommentStatus.Rejected);

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanDeleteComments)]
    public async Task<IActionResult> Delete(int[] commentIds)
    {
        var ids = commentIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return RedirectToAction(nameof(Index));
        }

        var comments = await db.DocumentComments
            .Include(c => c.Replies)
            .Where(c => ids.Contains(c.Id))
            .ToListAsync();

        var repliesRemovedWithParents = comments
            .Where(c => c.ParentCommentId == null)
            .SelectMany(c => c.Replies)
            .ToList();
        var repliesRemovedWithParentIds = repliesRemovedWithParents.Select(c => c.Id).ToHashSet();
        var directlySelectedReplies = comments
            .Where(c => c.ParentCommentId != null && !repliesRemovedWithParentIds.Contains(c.Id))
            .ToList();
        var selectedRoots = comments.Where(c => c.ParentCommentId == null).ToList();

        db.DocumentComments.RemoveRange(repliesRemovedWithParents);
        db.DocumentComments.RemoveRange(directlySelectedReplies);
        db.DocumentComments.RemoveRange(selectedRoots);
        await db.SaveChangesAsync();

        TempData["CommentManagementMessage"] = "deleted";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AppPermissionNames.CanModerateComments)]
    public async Task<IActionResult> Reply(int commentId, string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 1000)
        {
            return BadRequest();
        }

        var comment = await db.DocumentComments.FirstOrDefaultAsync(c => c.Id == commentId);
        if (comment == null)
        {
            return NotFound();
        }

        if (comment.ParentCommentId != null || comment.Status != CommentStatus.Approved)
        {
            return BadRequest();
        }

        var moderatorUserId = userManager.GetUserId(User)!;
        db.DocumentComments.Add(new DocumentComment
        {
            DocumentId = comment.DocumentId,
            UserId = moderatorUserId,
            ParentCommentId = comment.Id,
            Content = content.Trim(),
            CreatedAt = DateTime.UtcNow,
            Status = CommentStatus.Approved,
            ModeratedAtUtc = DateTime.UtcNow,
            ModeratedByUserId = moderatorUserId
        });
        await db.SaveChangesAsync();

        TempData["CommentManagementMessage"] = "replied";
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> SetStatus(int[] commentIds, CommentStatus status)
    {
        var ids = commentIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return RedirectToAction(nameof(Index));
        }

        var comments = await db.DocumentComments
            .Where(c => ids.Contains(c.Id))
            .ToListAsync();
        var moderatorUserId = userManager.GetUserId(User);
        var moderatedAt = DateTime.UtcNow;
        foreach (var comment in comments)
        {
            comment.Status = status;
            comment.ModeratedAtUtc = moderatedAt;
            comment.ModeratedByUserId = moderatorUserId;
        }

        await db.SaveChangesAsync();
        TempData["CommentManagementMessage"] = status == CommentStatus.Approved ? "approved" : "rejected";
        return RedirectToAction(nameof(Index));
    }
}
