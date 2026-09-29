using Aiursoft.AgentKit;
using Aiursoft.AgentKit.Messages;

namespace Aiursoft.DocsViewer.AgentExam;

public sealed class ScriptedExamModel : IAgentModelClient
{
    private readonly IReadOnlyList<ExamTurn> turns;
    private int turnIndex;
    private int replyIndex;
    private bool failed;

    public ScriptedExamModel(IReadOnlyList<ExamTurn> turns) => this.turns = turns;

    public void BeginTurn(int index)
    {
        if (failed || index != turnIndex || replyIndex != 0) throw new InvalidOperationException("Replay turn order mismatch.");
    }

    public void EndTurn()
    {
        if (replyIndex != turns[turnIndex].Replay.Count) throw new InvalidOperationException("Unconsumed scripted model responses.");
        turnIndex++;
        replyIndex = 0;
    }

    public ValueTask<AgentModelResponse> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (turnIndex >= turns.Count || replyIndex >= turns[turnIndex].Replay.Count)
        {
            failed = true;
            throw new InvalidOperationException("Scripted model responses exhausted.");
        }
        if (!request.Tools.Any(tool => tool.Name == "search_documents") ||
            !request.Transcript.Any(message => message.Role == TranscriptRole.User &&
                message.Content.OfType<TextBlock>().Any(block => block.Text == turns[turnIndex].Question)))
            throw new InvalidOperationException("Unexpected model request.");
        var reply = turns[turnIndex].Replay[replyIndex++];
        var content = new List<AgentContentBlock>();
        if (reply.Text is not null) content.Add(new TextBlock(reply.Text));
        foreach (var call in reply.Calls)
        {
            if (!request.Tools.Any(tool => tool.Name == call.Name)) throw new InvalidOperationException("Unknown replay tool.");
            content.Add(new ToolCallBlock(new ToolCall(call.Id, call.Name, call.Arguments.DeepClone())));
        }
        var reason = reply.FinishReason switch
        {
            "tool_calls" => AgentFinishReason.ToolCalls,
            "length" => AgentFinishReason.Length,
            "refusal" => AgentFinishReason.Refusal,
            _ => AgentFinishReason.Stop
        };
        return ValueTask.FromResult(new AgentModelResponse(content, reason));
    }

    public bool Complete => !failed && turnIndex == turns.Count && replyIndex == 0;
}
