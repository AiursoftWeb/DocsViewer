using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Aiursoft.AgentKit;
using Aiursoft.AgentKit.Messages;
using Aiursoft.DocsViewer.Configuration;

namespace Aiursoft.DocsViewer.Services.Agents;

public sealed class OpenAiCompatibleAgentModelClient(
    IHttpClientFactory httpClientFactory,
    GlobalSettingsService settings,
    ILogger<OpenAiCompatibleAgentModelClient> logger) : IAgentModelClient
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new Newtonsoft.Json.Serialization.SnakeCaseNamingStrategy() is { } naming
            ? new Newtonsoft.Json.Serialization.DefaultContractResolver { NamingStrategy = naming }
            : new Newtonsoft.Json.Serialization.DefaultContractResolver(),
        NullValueHandling = NullValueHandling.Ignore
    };

    public async ValueTask<AgentModelResponse> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        var model = (await settings.GetSettingValueAsync(SettingsMap.OpenAiAgentModel)).Trim();
        var endpoint = (await settings.GetSettingValueAsync(SettingsMap.OpenAiInstance)).Trim();
        var token = await settings.GetSettingValueAsync(SettingsMap.OpenAiApiToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(model)) throw new AgentModelClientException();
        try
        {
            var payload = new ChatRequest(model, request.Transcript.SelectMany(ToWireMessages).ToArray(), request.Tools.Select(ToWireTool).ToArray());
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            message.Content = new StringContent(JsonConvert.SerializeObject(payload, JsonSettings), Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(token)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClientFactory.CreateClient("DocsViewerAgentModel").SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new AgentModelClientException();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = JsonConvert.DeserializeObject<ChatResponse>(body, JsonSettings);
            var choice = parsed?.Choices?.FirstOrDefault() ?? throw new AgentModelClientException();
            if (choice.Message is null) throw new AgentModelClientException();
            var blocks = new List<AgentContentBlock>();
            if (!string.IsNullOrEmpty(choice.Message.Content)) blocks.Add(new TextBlock(choice.Message.Content));
            foreach (var call in choice.Message.ToolCalls ?? [])
            {
                if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Function.Name)) throw new AgentModelClientException();
                var args = JToken.Parse(string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments);
                if (args is not JObject) throw new AgentModelClientException();
                blocks.Add(new ToolCallBlock(new ToolCall(call.Id, call.Function.Name, args)));
            }
            return new AgentModelResponse(blocks, choice.FinishReason switch { "tool_calls" => AgentFinishReason.ToolCalls, "length" => AgentFinishReason.Length, "refusal" or "content_filter" => AgentFinishReason.Refusal, "stop" => AgentFinishReason.Stop, _ => throw new AgentModelClientException() });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentModelClientException) { throw; }
        catch (Exception ex) { logger.LogWarning("Agent model request failed with {ErrorType}.", ex.GetType().Name); throw new AgentModelClientException(); }
    }

    private static IEnumerable<ChatMessage> ToWireMessages(TranscriptMessage message)
    {
        if (message.Role == TranscriptRole.Tool)
        {
            foreach (var result in message.ToolResults)
            {
                yield return new ChatMessage("tool", result.Output?.ToString(Formatting.None) ?? result.Error ?? string.Empty,
                    ToolCallId: result.CallId);
            }
            yield break;
        }
        var text = string.Concat(message.Content.OfType<TextBlock>().Select(x => x.Text));
        yield return message.Role switch
        {
            TranscriptRole.System => new("system", text),
            TranscriptRole.User => new("user", text),
            TranscriptRole.Assistant => new("assistant", text,
                message.ToolCalls.Count == 0 ? null : message.ToolCalls.Select(ToWireCall).ToArray()),
            _ => throw new AgentModelClientException()
        };
    }
    private static WireToolCall ToWireCall(ToolCall call) => new(call.Id, new(call.Name, call.Arguments.ToString(Formatting.None)));
    private static WireTool ToWireTool(ToolDefinition tool) => new("function", new(tool.Name, tool.Description,
        tool.InputSchema.DeepClone()));

    // ReSharper disable NotAccessedPositionalProperty.Local
    private sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<WireTool> Tools, bool Stream = false, int MaxTokens = 2048);
    private sealed record ChatMessage(string Role, string? Content, IReadOnlyList<WireToolCall>? ToolCalls = null, string? ToolCallId = null);
    private sealed record WireTool(string Type, WireFunction Function);
    private sealed record WireFunction(string Name, string Description, JToken Parameters);
    private sealed record WireToolCall(string Id, WireFunctionCall Function, string Type = "function");
    // ReSharper restore NotAccessedPositionalProperty.Local
    private sealed record WireFunctionCall(string Name, string Arguments);
    private sealed record ChatResponse(List<Choice>? Choices);
    private sealed record Choice(WireAssistantMessage? Message, string? FinishReason);
    private sealed record WireAssistantMessage(string? Content, List<WireToolCall>? ToolCalls);
}

public sealed class AgentModelClientException() : Exception("The agent model request could not be completed.");
