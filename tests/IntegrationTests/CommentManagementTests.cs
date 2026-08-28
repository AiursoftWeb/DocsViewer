using System.Net;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Services;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Tests.IntegrationTests;

[TestClass]
public class CommentManagementTests : TestBase
{
    [TestMethod]
    public async Task ModeratorCanReviewReplyAndDeleteCommentThread()
    {
        var documentId = await AddDocumentAsync();
        await RegisterAndLoginAsync();

        using (var scope = Server!.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
            await settings.UpdateSettingAsync(SettingsMap.RequireCommentReview, "True");
        }

        const string content = "A unique pending comment for moderation";
        var postResponse = await PostForm("/Comments/Post", new Dictionary<string, string>
        {
            { "documentId", documentId.ToString() },
            { "content", content }
        }, tokenUrl: "/moderation-test.md");
        Assert.AreEqual(HttpStatusCode.Found, postResponse.StatusCode);

        int commentId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocsViewerDbContext>();
            var comment = await db.DocumentComments.SingleAsync(c => c.Content == content);
            Assert.AreEqual(CommentStatus.Pending, comment.Status);
            commentId = comment.Id;
        }

        var publicResponse = await Http.GetAsync("/moderation-test.md");
        publicResponse.EnsureSuccessStatusCode();
        var publicHtml = await publicResponse.Content.ReadAsStringAsync();
        Assert.IsFalse(publicHtml.Contains(content, StringComparison.Ordinal));

        await LoginAsAdmin();
        var managementResponse = await Http.GetAsync("/CommentManagement/Index?status=Pending");
        managementResponse.EnsureSuccessStatusCode();
        var managementHtml = await managementResponse.Content.ReadAsStringAsync();
        Assert.Contains(content, managementHtml);

        var approveResponse = await PostForm("/CommentManagement/Approve", new Dictionary<string, string>
        {
            { "commentIds", commentId.ToString() }
        }, tokenUrl: "/CommentManagement/Index");
        Assert.AreEqual(HttpStatusCode.Found, approveResponse.StatusCode);

        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocsViewerDbContext>();
            var approved = await db.DocumentComments.SingleAsync(c => c.Id == commentId);
            Assert.AreEqual(CommentStatus.Approved, approved.Status);
            Assert.IsNotNull(approved.ModeratedAtUtc);
            Assert.IsNotNull(approved.ModeratedByUserId);
        }

        publicResponse = await Http.GetAsync("/moderation-test.md");
        publicHtml = await publicResponse.Content.ReadAsStringAsync();
        Assert.Contains(content, publicHtml);

        var rejectResponse = await PostForm("/CommentManagement/Reject", new Dictionary<string, string>
        {
            { "commentIds", commentId.ToString() }
        }, tokenUrl: "/CommentManagement/Index");
        Assert.AreEqual(HttpStatusCode.Found, rejectResponse.StatusCode);

        publicResponse = await Http.GetAsync("/moderation-test.md");
        publicHtml = await publicResponse.Content.ReadAsStringAsync();
        Assert.IsFalse(publicHtml.Contains(content, StringComparison.Ordinal));

        await PostForm("/CommentManagement/Approve", new Dictionary<string, string>
        {
            { "commentIds", commentId.ToString() }
        }, tokenUrl: "/CommentManagement/Index");

        const string replyContent = "An approved administrator reply";
        var replyResponse = await PostForm("/CommentManagement/Reply", new Dictionary<string, string>
        {
            { "commentId", commentId.ToString() },
            { "content", replyContent }
        }, tokenUrl: "/CommentManagement/Index");
        Assert.AreEqual(HttpStatusCode.Found, replyResponse.StatusCode);

        int replyId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocsViewerDbContext>();
            var reply = await db.DocumentComments.SingleAsync(c => c.Content == replyContent);
            Assert.AreEqual(commentId, reply.ParentCommentId);
            Assert.AreEqual(CommentStatus.Approved, reply.Status);
            replyId = reply.Id;
        }

        var deleteResponse = await PostForm("/CommentManagement/Delete", new Dictionary<string, string>
        {
            { "commentIds[0]", commentId.ToString() },
            { "commentIds[1]", replyId.ToString() }
        }, tokenUrl: "/CommentManagement/Index");
        Assert.AreEqual(HttpStatusCode.Found, deleteResponse.StatusCode);

        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocsViewerDbContext>();
            Assert.IsFalse(await db.DocumentComments.AnyAsync(c => c.Id == commentId || c.ParentCommentId == commentId));
        }
    }

    [TestMethod]
    public async Task CommentManagementRequiresAuthentication()
    {
        var response = await Http.GetAsync("/CommentManagement/Index");
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.OriginalString ?? string.Empty);

        await RegisterAndLoginAsync();
        response = await Http.GetAsync("/CommentManagement/Index");
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/Error/Code403", response.Headers.Location?.OriginalString ?? string.Empty);
    }

    private async Task<int> AddDocumentAsync()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocsViewerDbContext>();
        var document = new Document
        {
            FilePath = "moderation-test.md",
            Content = "# Moderation Test",
            Title = "Moderation Test",
            Category = "root",
            SourceCulture = "en-US",
            FileLastModified = DateTime.UtcNow
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document.Id;
    }
}
