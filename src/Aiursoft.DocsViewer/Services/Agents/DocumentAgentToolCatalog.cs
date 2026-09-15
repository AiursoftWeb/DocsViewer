using Aiursoft.AgentKit;

namespace Aiursoft.DocsViewer.Services.Agents;

/// <summary>
/// DocsViewer's application-owned agent tool policy for one turn. Tool discovery and
/// invocation remain extensible, while exposure, risk, approval UX and public progress
/// are decided by DocsViewer rather than by an MCP attribute or model prompt.
/// </summary>
public enum DocumentAgentToolRisk
{
    ReadOnly,
    Mutating,
    Destructive,
    ExternalSideEffect
}

public sealed record DocumentAgentToolPolicy(
    string Name,
    DocumentAgentToolRisk Risk,
    bool RequiresApproval,
    bool ExposeToModel);

public interface IDocumentAgentToolCatalog
{
    IReadOnlyList<IAgentTool> GetTools();
}

public sealed class DocumentAgentToolCatalog(DocumentSearchAgentTool searchTool) : IDocumentAgentToolCatalog
{
    private static readonly IReadOnlyDictionary<string, DocumentAgentToolPolicy> Policies =
        new Dictionary<string, DocumentAgentToolPolicy>(StringComparer.Ordinal)
        {
            [DocumentSearchAgentTool.Name] = new(
                DocumentSearchAgentTool.Name,
                DocumentAgentToolRisk.ReadOnly,
                RequiresApproval: false,
                ExposeToModel: true)
        };

    public IReadOnlyList<IAgentTool> GetTools()
    {
        var tools = new IAgentTool[] { searchTool };
        foreach (var tool in tools)
        {
            if (!Policies.TryGetValue(tool.Definition.Name, out var policy) ||
                !policy.ExposeToModel ||
                policy.RequiresApproval ||
                policy.Risk != DocumentAgentToolRisk.ReadOnly)
            {
                throw new InvalidOperationException("The DocsViewer agent catalog contains an unsupported tool policy.");
            }
        }

        return tools;
    }
}
