using Newtonsoft.Json.Linq;

namespace Aiursoft.DocsViewer.AgentExam;

public sealed class ExamScenario
{
    public required string SchemaVersion { get; init; }
    public required string Id { get; init; }
    public double Weight { get; init; } = 1;
    public int TimeoutSeconds { get; init; } = 90;
    public string Culture { get; init; } = "en-US";
    public string PathBase { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public List<ExamDocument> Documents { get; init; } = [];
    public required List<ExamTurn> Turns { get; init; }
}

public sealed class ExamDocument
{
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required string Path { get; init; }
    public required string Content { get; init; }
}

public sealed class ExamTurn
{
    public required string Question { get; init; }
    public required List<ExamAssertion> Assertions { get; init; }
    public List<ExamReply> Replay { get; init; } = [];
}

public sealed class ExamAssertion
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Dimension { get; init; }
    public double Points { get; init; } = 1;
    public double Penalty { get; init; }
    public bool Required { get; init; } = true;
    public bool HardFail { get; init; }
    public required JToken Match { get; init; }
}

public sealed class ExamReply
{
    public string FinishReason { get; init; } = "stop";
    public string? Text { get; init; }
    public List<ExamCall> Calls { get; init; } = [];
}

public sealed class ExamCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required JObject Arguments { get; init; }
}
