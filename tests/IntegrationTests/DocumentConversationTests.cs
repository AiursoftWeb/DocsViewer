using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aiursoft.CSTools.Tools;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Services.Agents;

namespace Aiursoft.DocsViewer.Tests.IntegrationTests;

[TestClass]
public sealed class DocumentConversationTests : TestBase
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackgroundPostReturnsBeforeProviderAndContinuesWithStableCitations(bool cancelWhileBlocked)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentQueue<JsonElement>();
        var keyword = "Zircon" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Network.GetAvailablePort()}");
        await using var provider = builder.Build();
        provider.MapPost("/v1/chat/completions", async (HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            requests.Enqueue(body.RootElement.Clone());
            var count = requests.Count;
            if (count == 1) { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            if (count % 2 == 1)
            {
                var call = new { id = $"call-{count}", type = "function", function = new { name = "search_documents", arguments = JsonSerializer.Serialize(new { query = keyword }) } };
                var choice = new { finish_reason = "tool_calls", message = new { role = "assistant", tool_calls = new[] { call } } };
                return Results.Json(new { choices = new[] { choice } });
            }
            return Results.Json(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = $"The procedure uses a release switch. [D{count / 2}]" } } } });
        });
        await provider.StartAsync();
        try
        {
            using (var scope = Server!.Services.CreateScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
                await settings.UpdateSettingAsync(SettingsMap.OpenAiInstance, provider.Urls.Single() + "/v1/chat/completions");
                await settings.UpdateSettingAsync(SettingsMap.OpenAiAgentModel, "test-agent");
                await settings.UpdateSettingAsync(SettingsMap.OpenAiApiToken, "");
                await settings.UpdateSettingAsync(SettingsMap.EnableEmbeddingBasedSearch, "False");
                var db = scope.ServiceProvider.GetRequiredService<DocsViewerDbContext>();
                db.Documents.Add(new Document { Title = keyword, Content = "The procedure uses a release switch.", FilePath = $"guides/{keyword}.md", Category = "guides", SourceCulture = "en-US", FileLastModified = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            await LoginAsAdmin();
            var token = await GetAntiCsrfToken("/Agent");
            async Task<HttpResponseMessage> Send(string message, Guid? id)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/Agent/SendMessage") { Content = JsonContent.Create(new { Message = message, ConversationId = id }) };
                req.Headers.Add("RequestVerificationToken", token);
                return await Http.SendAsync(req);
            }
            var response = await Send("Explain the procedure", null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ConversationId").GetGuid();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(release.Task.IsCompleted);
            var initial = await Http.GetFromJsonAsync<JsonElement>($"/Agent/Status?conversationId={id}");
            Assert.AreEqual("Thinking", initial.GetProperty("State").GetString());
            Assert.AreEqual(1, initial.GetProperty("Messages").GetArrayLength());
            var coordinator = GetService<DocumentConversationService>();
            Assert.IsNull(coordinator.Status("other-user", id));
            Assert.IsFalse(coordinator.Cancel("other-user", id));
            Assert.AreEqual("NotFound", (await coordinator.SendAsync("other-user", "steal", id, "en-US", "")).Error);
            if (cancelWhileBlocked)
            {
                var cancel = new HttpRequestMessage(HttpMethod.Post, $"/Agent/Cancel?conversationId={id}");
                cancel.Headers.Add("RequestVerificationToken", token);
                Assert.AreEqual(HttpStatusCode.OK, (await Http.SendAsync(cancel)).StatusCode);
                var cancelled = await WaitTerminal(id);
                Assert.AreEqual("Cancelled", cancelled.GetProperty("State").GetString());
                Assert.AreEqual(1, cancelled.GetProperty("Messages").GetArrayLength());
                release.TrySetResult();
                Assert.AreEqual("Cancelled", (await Http.GetFromJsonAsync<JsonElement>($"/Agent/Status?conversationId={id}")).GetProperty("State").GetString());
                return;
            }
            release.TrySetResult();
            var complete = await WaitTerminal(id);
            Assert.AreEqual("Completed", complete.GetProperty("State").GetString());
            Assert.AreEqual("[D1]", complete.GetProperty("Messages")[1].GetProperty("Citations")[0].GetProperty("Label").GetString());
            Assert.IsFalse(complete.GetProperty("Messages")[1].GetProperty("Citations")[0].TryGetProperty("Excerpt", out _));
            Assert.AreEqual(HttpStatusCode.OK, (await Send("What switch was that?", id)).StatusCode);
            var followup = await WaitTerminal(id);
            Assert.AreEqual(4, followup.GetProperty("Messages").GetArrayLength());
            Assert.AreEqual("[D2]", followup.GetProperty("Messages")[3].GetProperty("Citations")[0].GetProperty("Label").GetString());
            Assert.IsTrue(requests.ToArray()[2].GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "tool"));
            var noToken = await Http.PostAsJsonAsync("/Agent/SendMessage", new { Message = "bad" });
            Assert.AreEqual(HttpStatusCode.BadRequest, noToken.StatusCode);
        }
        finally { release.TrySetResult(); await provider.StopAsync(); }
    }

    private async Task<JsonElement> WaitTerminal(Guid id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var state = await Http.GetFromJsonAsync<JsonElement>($"/Agent/Status?conversationId={id}", deadline.Token);
            if (state.GetProperty("State").GetString() is "Completed" or "Error" or "Cancelled") return state;
            await Task.Delay(100, deadline.Token);
        }
    }
}
