using Aiursoft.AgentKit;
using Aiursoft.AgentKit.Messages;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DocsViewer.Services.Agents;

/// <summary>
/// Process-local agent execution metadata returned only to the conversation owner through
/// Status. Clients must not render entries marked <see cref="IsMeta"/>.
/// </summary>
public sealed record AgentDiagnosticEvent(
    long Sequence,
    long Generation,
    int Iteration,
    string Kind,
    bool IsMeta = true,
    string? Text = null,
    string? ToolCallId = null,
    string? ToolName = null,
    JToken? Arguments = null,
    JToken? Output = null,
    ToolOutcome? ToolOutcome = null,
    AgentRunOutcome? RunOutcome = null,
    string? FailureCategory = null)
{
    public AgentDiagnosticEvent DeepCopy() => this with
    {
        Arguments = Arguments?.DeepClone(),
        Output = Output?.DeepClone()
    };
}
