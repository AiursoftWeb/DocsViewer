namespace Aiursoft.DocsViewer.ExamRunner.Reporting;

public sealed record AssertionReport(string Id, string Dimension, bool Matched);
public sealed record ScenarioReport(string Id, bool Valid, bool Passed, double Score, double ElapsedSeconds,
    string? FailureKind, IReadOnlyList<AssertionReport> Assertions);
public sealed record DimensionReport(string Dimension, double Score, double Weight, double Contribution);
public sealed record RepetitionReport(string SchemaVersion, string CandidateId, string Model, int Repetition,
    double Total, bool Incomplete, IReadOnlyList<DimensionReport> Dimensions, IReadOnlyList<ScenarioReport> Scenarios);
public sealed record DimensionSummary(string Dimension, double MeanScore, double MeanContribution);
public sealed record CandidateSummary(string Id, string Model, int Repetitions, double Mean, double Minimum,
    double Maximum, double StandardDeviation, double CompletionRate, int IncompleteRuns, int InvalidScenarios,
    IReadOnlyList<DimensionSummary> Dimensions);
public sealed record SummaryReport(string SchemaVersion, DateTimeOffset StartedAt, double FailBelow, bool Passed,
    IReadOnlyList<CandidateSummary> Candidates);
