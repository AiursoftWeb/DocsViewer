namespace Aiursoft.DocsViewer.ExamRunner.Configuration;

public sealed record ExamConfiguration
{
    public required string SchemaVersion { get; init; }
    public required string[] Scenarios { get; init; }
    public string OutputDirectory { get; init; } = "reports";
    public double FailBelow { get; init; } = 70;
    public required CandidateConfiguration[] Candidates { get; init; }
}

public sealed record CandidateConfiguration
{
    public required string Id { get; init; }
    public required string Endpoint { get; init; }
    public required string Model { get; init; }
    public int Repetitions { get; init; } = 1;
    public required CandidateAuthentication Authentication { get; init; }
}

public sealed record CandidateAuthentication
{
    public required string Mode { get; init; }
    public string? EnvironmentVariable { get; init; }
}

public sealed record LoadedCandidate(CandidateConfiguration Candidate, string? Credential);

public sealed record LoadedExamConfiguration(
    ExamConfiguration Configuration,
    IReadOnlyList<string> ScenarioPaths,
    string OutputDirectory,
    IReadOnlyList<LoadedCandidate> Candidates);
