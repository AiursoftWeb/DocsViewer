using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Aiursoft.CSTools.Tools;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Models.AgentViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Aiursoft.DocsViewer.Tests.IntegrationTests;

[TestClass]
public sealed class AgentControllerTests : TestBase
{
    [TestMethod]
    public async Task AnonymousAgentPageRequiresLogin()
    {
        var response = await Http.GetAsync("/Agent");
        AssertRedirect(response, "/Account/Login", exact: false);
    }

    [TestMethod]
    public async Task AuthenticatedPageRendersFormAndNavigationWithoutConfiguration()
    {
        await LoginAsAdmin();
        var response = await Http.GetAsync("/Agent");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Document Assistant", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.Contains("The assistant is not configured", html);
        Assert.Contains("maxlength=\"2000\"", html);
        Assert.Contains("node_modules/@aiursoft/uistack-markdown-ui/dist/index.global.js", html);
        Assert.Contains("styles/markdown-reader.css", html);
        Assert.Contains("scripts/agent-chat.js", html);
        Assert.Contains("Press Enter to send and Shift+Enter for a new line.", html);
        Assert.Contains("/Agent", html);
    }

    [TestMethod]
    public async Task PostRequiresAntiforgery()
    {
        await LoginAsAdmin();
        var response = await PostForm("/Agent", new() { ["Question"] = "hello" }, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UnconfiguredPostEncodesQuestionAndNeverContactsProvider()
    {
        await LoginAsAdmin();
        var response = await PostForm("/Agent", new() { ["Question"] = "<script>alert(1)</script>" });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("The assistant is not configured", html);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConfiguredPostSearchesDocumentsAndValidatesCitations(bool inventCitation)
    {
        var keyword = $"Quartz{Guid.NewGuid():N}";
        var requests = new ConcurrentQueue<JsonElement>();
        var providerBuilder = WebApplication.CreateSlimBuilder();
        providerBuilder.WebHost.UseUrls($"http://127.0.0.1:{Network.GetAvailablePort()}");
        await using var provider = providerBuilder.Build();
        provider.MapPost("/v1/chat/completions", async (HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            requests.Enqueue(body.RootElement.Clone());
            if (requests.Count == 1)
            {
                return Results.Json(new
                {
                    choices = new[]
                    {
                        new
                        {
                            finish_reason = "tool_calls",
                            message = new
                            {
                                role = "assistant",
                                tool_calls = new[]
                                {
                                    new
                                    {
                                        id = "search-1",
                                        type = "function",
                                        function = new { name = "search_documents", arguments = JsonSerializer.Serialize(new { query = $"{keyword} deployment" }) }
                                    }
                                }
                            }
                        }
                    }
                });
            }

            return Results.Json(new
            {
                choices = new[]
                {
                    new
                    {
                        finish_reason = "stop",
                        message = new
                        {
                            role = "assistant",
                            content = inventCitation
                                ? "Unsupported provider claim [D999]"
                                : "Quartz deployment requires the release switch. [D1]"
                        }
                    }
                }
            });
        });
        await provider.StartAsync();

        try
        {
            using (var scope = Server!.Services.CreateScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
                await settings.UpdateSettingAsync(SettingsMap.OpenAiInstance, $"{provider.Urls.Single()}/v1/chat/completions");
                await settings.UpdateSettingAsync(SettingsMap.OpenAiAgentModel, "local-test-model");
                await settings.UpdateSettingAsync(SettingsMap.OpenAiApiToken, string.Empty);
                // Force deterministic lexical retrieval without making any embedding requests.
                await settings.UpdateSettingAsync(SettingsMap.EnableEmbeddingBasedSearch, "False");
                var db = scope.ServiceProvider.GetRequiredService<DocsViewerDbContext>();
                db.Documents.Add(new Document
                {
                    FilePath = $"guides/{keyword}/quartz.md",
                    Title = $"{keyword} deployment guide",
                    Content = "Quartz deployment requires the release switch.",
                    Category = "guides",
                    SourceCulture = "en-US",
                    FileLastModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                });
                await db.SaveChangesAsync();
            }

            await LoginAsAdmin();
            var response = await PostForm("/Agent", new() { ["Question"] = "What does Quartz deployment require?" });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(2, requests.Count);
            var calls = requests.ToArray();
            Assert.AreEqual("local-test-model", calls[0].GetProperty("model").GetString());
            Assert.AreEqual("search_documents", calls[0].GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
            var toolMessage = calls[1].GetProperty("messages").EnumerateArray()
                .Single(message => message.GetProperty("role").GetString() == "tool");
            Assert.AreEqual("search-1", toolMessage.GetProperty("tool_call_id").GetString());
            using var evidence = JsonDocument.Parse(toolMessage.GetProperty("content").GetString()!);
            // Keyword search matches any term; the unique title term must rank this document first.
            var document = evidence.RootElement.GetProperty("results").EnumerateArray().First();
            Assert.AreEqual($"{keyword} deployment guide", document.GetProperty("title").GetString());
            Assert.AreEqual("[D1]", document.GetProperty("citation").GetString());
            Assert.AreEqual("Quartz deployment requires the release switch.", document.GetProperty("excerpt").GetString());

            if (inventCitation)
            {
                Assert.Contains("The documentation does not provide enough evidence", html);
                Assert.DoesNotContain("Unsupported provider claim", html);
                Assert.DoesNotContain("/Documents/Detail?path=guides", html);
            }
            else
            {
                Assert.Contains("Quartz deployment requires the release switch. [D1]", html);
                Assert.Contains($"{keyword} deployment guide", html);
                Assert.Contains("/Documents/Detail?path=guides", html);
                Assert.Contains("quartz.md", html);
            }
        }
        finally
        {
            await provider.StopAsync();
        }
    }

    [TestMethod]
    public async Task AnswerAndCitationTextAreHtmlEncoded()
    {
        using var scope = Server!.Services.CreateScope();
        var services = scope.ServiceProvider;
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "test"));
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var engine = services.GetRequiredService<IRazorViewEngine>();
        var view = engine.GetView(null, "/Views/Agent/Index.cshtml", false);
        Assert.IsTrue(view.Success);
        var razorView = view.View ?? throw new InvalidOperationException("The Agent view could not be loaded.");
        var data = new ViewDataDictionary(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = new IndexViewModel
            {
                Configured = true,
                Answer = "<script>alert('answer')</script> [D1]",
                Citations = [new("[D1]", "<img src=x onerror=alert(1)>", "file.md", "excerpt", "/Documents/Detail?path=file.md")]
            }
        };
        var temp = new TempDataDictionary(http, services.GetRequiredService<ITempDataProvider>());
        using var writer = new StringWriter();
        var context = new ViewContext(action, razorView, data, temp, writer, new HtmlHelperOptions());
        await razorView.RenderAsync(context);
        var html = writer.ToString();
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;img", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img src=x", html);
    }

    [TestMethod]
    public async Task OversizedAndBlankQuestionsAreRejectedBeforeProviderCall()
    {
        await LoginAsAdmin();
        using var scope = Server!.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(SettingsMap.OpenAiInstance, "http://127.0.0.1:1/v1/chat/completions");
        await settings.UpdateSettingAsync(SettingsMap.OpenAiAgentModel, "test");
        foreach (var question in new[] { " ", new string('x', 2001) })
        {
            var response = await PostForm("/Agent", new() { ["Question"] = question });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("validation-summary-errors", html);
            Assert.DoesNotContain("The assistant could not complete", html);
        }
    }
}
