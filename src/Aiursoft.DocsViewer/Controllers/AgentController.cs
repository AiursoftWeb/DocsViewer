using System.Security.Claims;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Models.AgentViewModels;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Services.Agents;
using Aiursoft.UiStack.Navigation;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Aiursoft.DocsViewer.Controllers;

[Authorize]
[LimitPerMin]
public sealed class AgentController(
    GlobalSettingsService settings,
    GroundedDocumentAnswerService answers,
    DocumentConversationService conversations,
    IStringLocalizer<AgentController> localizer) : Controller
{
    [HttpGet]
    [RenderInNavBar(NavGroupName = "Documentation", NavGroupOrder = 1,
        CascadedLinksGroupName = "Explore", CascadedLinksIcon = "book-open", CascadedLinksOrder = 1,
        LinkText = "Document Assistant", LinkOrder = 1)]
    public async Task<IActionResult> Index() => this.StackView(await CreateModelAsync());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index([Bind(nameof(IndexViewModel.Question))] IndexViewModel input,
        CancellationToken cancellationToken)
    {
        var model = await CreateModelAsync();
        model.Question = input.Question;
        if (string.IsNullOrWhiteSpace(input.Question) && ModelState.IsValid)
            ModelState.AddModelError(nameof(input.Question), localizer["Please enter a question."]);
        if (!ModelState.IsValid || !model.Configured) return this.StackView(model);
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();
        try
        {
            var result = await answers.AnswerAsync(userId, input.Question.Trim(), cancellationToken);
            switch (result.Status)
            {
                case DocumentAnswerStatus.Completed:
                    model.Answer = result.Answer;
                    model.Citations = result.Citations;
                    break;
                case DocumentAnswerStatus.InsufficientEvidence:
                    model.StatusMessage = localizer["The documentation does not provide enough evidence to answer this question."];
                    break;
                case DocumentAnswerStatus.RateLimited:
                    model.StatusMessage = localizer["Too many questions. Please wait a minute before trying again."];
                    break;
                case DocumentAnswerStatus.Busy:
                    model.StatusMessage = localizer["The assistant is busy or you already have a question in progress. Please try again shortly."];
                    break;
                default:
                    model.StatusMessage = localizer["The assistant could not complete this request. Please try again later."];
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        return this.StackView(model);
    }

    public sealed record SendMessageRequest(string Message, Guid? ConversationId);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 2000)
            return ConversationJson(new { ErrorMessage = localizer["Please enter a question of at most 2000 characters."].Value }, 400);
        if (!(await CreateModelAsync()).Configured)
            return ConversationJson(new { ErrorMessage = localizer["The assistant is not configured."].Value }, 400);
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(owner)) return Challenge();
        var result = await conversations.SendAsync(owner, input.Message, input.ConversationId,
            System.Globalization.CultureInfo.CurrentCulture.Name, Request.PathBase.Value ?? string.Empty, cancellationToken);
        if (result.Error == "NotFound") return NotFound();
        if (result.Error is not null)
            return ConversationJson(
                new { ErrorMessage = localizer["The request could not be started. Wait a moment or start a new conversation."].Value },
                result.Error is "Busy" or "RateLimited" ? 429 : 400);
        return ConversationJson(new { result.ConversationId });
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Status(Guid conversationId)
    {
        var result = conversations.Status(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, conversationId);
        if (result is null) return NotFound();
        return ConversationJson(result with { ErrorMessage = result.ErrorMessage is null ? null :
            localizer["The assistant could not complete this turn. Please try again or start a new conversation."].Value });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Cancel(Guid conversationId) =>
        conversations.Cancel(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, conversationId)
            ? ConversationJson(new { success = true }) : NotFound();

    private static JsonResult ConversationJson(object value, int statusCode = 200) => new(value,
        new Newtonsoft.Json.JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver()
        }) { StatusCode = statusCode };

    private async Task<IndexViewModel> CreateModelAsync()
    {
        var endpoint = await settings.GetSettingValueAsync(SettingsMap.OpenAiInstance);
        var agentModel = await settings.GetSettingValueAsync(SettingsMap.OpenAiAgentModel);
        return new IndexViewModel
        {
            PageTitle = localizer["Document Assistant"],
            Configured = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                uri.Scheme is "https" or "http" && !string.IsNullOrWhiteSpace(agentModel)
        };
    }
}
