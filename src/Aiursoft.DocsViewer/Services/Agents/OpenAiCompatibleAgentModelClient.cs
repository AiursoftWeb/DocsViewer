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
        var endpoint = (await settings.GetSettingValueAsync(SettingsMap.OpenAiAgentInstance)).Trim();
        var token = await settings.GetSettingValueAsync(SettingsMap.OpenAiAgentApiToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(model))
        {
            logger.LogWarning("Agent model request rejected due to {FailureKind}. HasEndpoint: {HasEndpoint}; hasModel: {HasModel}.",
                "InvalidConfiguration", !string.IsNullOrWhiteSpace(endpoint), !string.IsNullOrWhiteSpace(model));
            throw new AgentModelClientException();
        }
        var endpointAuthority = uri.GetLeftPart(UriPartial.Authority);
        try
        {
            var payload = new ChatRequest(model, request.Transcript.SelectMany(ToWireMessages).ToArray(), request.Tools.Select(ToWireTool).ToArray());
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            message.Content = new StringContent(JsonConvert.SerializeObject(payload, JsonSettings), Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(token)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClientFactory.CreateClient("DocsViewerAgentModel").SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw Fail("HttpStatus", endpointAuthority, model, statusCode: (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            ChatResponse? parsed;
            try { parsed = JsonConvert.DeserializeObject<ChatResponse>(body, JsonSettings); }
            catch (Exception ex) { throw Fail("InvalidJson", endpointAuthority, model, ex.GetType().Name); }
            var choice = parsed?.Choices?.FirstOrDefault() ?? throw Fail("MissingChoice", endpointAuthority, model);
            if (choice.Message is null) throw Fail("MissingMessage", endpointAuthority, model);
            var blocks = new List<AgentContentBlock>();
            if (!string.IsNullOrEmpty(choice.Message.Content)) blocks.Add(new TextBlock(choice.Message.Content));
            foreach (var call in choice.Message.ToolCalls ?? [])
            {
                if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Function.Name))
                    throw Fail("InvalidToolCall", endpointAuthority, model);
                JToken args;
                try { args = JToken.Parse(string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments); }
                catch (Exception ex) { throw Fail("InvalidToolArguments", endpointAuthority, model, ex.GetType().Name); }
                if (args is not JObject) throw Fail("InvalidToolArguments", endpointAuthority, model);
                blocks.Add(new ToolCallBlock(new ToolCall(call.Id, call.Function.Name, args)));
            }
            return new AgentModelResponse(blocks, choice.FinishReason switch { "tool_calls" => AgentFinishReason.ToolCalls, "length" => AgentFinishReason.Length, "refusal" or "content_filter" => AgentFinishReason.Refusal, "stop" => AgentFinishReason.Stop, _ => throw Fail("UnsupportedFinishReason", endpointAuthority, model) });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentModelClientException) { throw; }
        catch (Exception ex)
        {
            throw Fail("TransportOrResponseProcessing", endpointAuthority, model, ex.GetType().Name);
        }
    }

    private AgentModelClientException Fail(string failureKind, string endpointAuthority, string model, string? errorType = null, int? statusCode = null)
    {
        logger.LogWarning(
            "Agent model request failed during {FailureKind}. StatusCode: {StatusCode}; error type: {ErrorType}; endpoint: {EndpointAuthority}; model: {Model}.",
            failureKind,
            statusCode,
            errorType,
            endpointAuthority,
            model);
        return new AgentModelClientException();
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
