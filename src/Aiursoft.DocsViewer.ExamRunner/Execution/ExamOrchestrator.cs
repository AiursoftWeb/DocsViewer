using System.Diagnostics;
using Aiursoft.AgentKit;
using Aiursoft.AgentKit.Evaluator;
using Aiursoft.DocsViewer.AgentExam;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.ExamRunner.Configuration;
using Aiursoft.DocsViewer.ExamRunner.Reporting;
using Aiursoft.DocsViewer.InMemory;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Services.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.DocsViewer.ExamRunner.Execution;

public sealed record ExamExecutionResult(int ExitCode, string OutputDirectory, SummaryReport Summary);

public sealed class ExamOrchestrator(
    Func<LoadedCandidate, IAgentModelClient>? modelFactory = null,
    Func<ExamScenario, IAgentModelClient, CancellationToken, Task<ExamAttempt>>? attemptFactory = null)
{
    private static readonly IReadOnlyDictionary<string, double> Weights = new Dictionary<string, double>
    {
        ["grounding"] = .5, ["safety"] = .3, ["efficiency"] = .2
    };

    public async Task<ExamExecutionResult> RunAsync(LoadedExamConfiguration loaded, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        var scenarios = new List<ExamScenario>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var files = loaded.ScenarioPaths.SelectMany(path => Directory.Exists(path)
                ? Directory.GetFiles(path, "*.json") : new[] { path })
            .Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var path in files)
        foreach (var scenario in ScenarioLoader.Load(path))
        {
            if (!ids.Add(scenario.Id)) throw new InvalidOperationException("Duplicate scenario ID across configured paths.");
            scenarios.Add(scenario);
        }
        if (scenarios.Count == 0) throw new InvalidOperationException("No exam scenarios were loaded.");
        if (scenarios.SelectMany(s => s.Turns).SelectMany(t => t.Assertions)
            .Any(a => !Weights.ContainsKey(a.Dimension)))
            throw new InvalidOperationException("Scenario uses an unsupported scoring dimension.");

        token.ThrowIfCancellationRequested();
        var startedAt = DateTimeOffset.UtcNow;
        var directory = ReserveDirectory(loaded.OutputDirectory, startedAt);
        var summaries = new List<CandidateSummary>();
        foreach (var candidate in loaded.Candidates)
        {
            var runs = new List<RepetitionReport>();
            for (var repetition = 1; repetition <= candidate.Candidate.Repetitions; repetition++)
            {
                token.ThrowIfCancellationRequested();
                var cases = new List<EvaluationCaseResult>();
                var scenarioReports = new List<ScenarioReport>();
                foreach (var scenario in scenarios)
                {
                    token.ThrowIfCancellationRequested();
                    var watch = Stopwatch.StartNew();
                    EvaluationCaseResult result;
                    try
                    {
                        var model = (modelFactory ?? CreateModel)(candidate);
                        try
                        {
                            var attempt = await (attemptFactory ?? ExamExecutor.RunAsync)(scenario, model, token);
                            result = attempt.Result;
                        }
                        finally
                        {
                            (model as IDisposable)?.Dispose();
                        }
                        watch.Stop();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception)
                    {
                        result = new AssertionEvaluator().Evaluate(ScenarioLoader.ToCase(scenario),
                            new EvaluationEvidence([], false, "ExecutionFailure"));
                    }
                    cases.Add(result);
                    scenarioReports.Add(new ScenarioReport(scenario.Id, result.Valid, result.Passed, result.Score,
                        watch.Elapsed.TotalSeconds, result.Valid ? null : "InvalidEvidence",
                        result.Assertions.Select(a => new AssertionReport(a.Id, a.Dimension, a.Matched)).ToArray()));
                }
                var score = EvaluationScoreCalculator.Calculate(cases, Weights);
                var report = new RepetitionReport("1.0", candidate.Candidate.Id, candidate.Candidate.Model, repetition,
                    score.Total, score.Incomplete, score.Dimensions.Select(d => new DimensionReport(
                        d.Dimension, d.Score, d.Weight, d.Contribution)).ToArray(), scenarioReports);
                runs.Add(report);
                await ExamReportWriter.WriteAsync(report,
                    Path.Combine(directory, candidate.Candidate.Id, $"repetition-{repetition}"), token);
            }
            var totals = runs.Select(run => run.Total).ToArray();
            var mean = totals.Average();
            summaries.Add(new CandidateSummary(candidate.Candidate.Id, candidate.Candidate.Model, runs.Count,
                mean, totals.Min(), totals.Max(), Math.Sqrt(totals.Average(total => Math.Pow(total - mean, 2))),
                runs.Count(run => !run.Incomplete) / (double)runs.Count, runs.Count(run => run.Incomplete),
                runs.Sum(run => run.Scenarios.Count(scenario => !scenario.Valid)),
                Weights.Keys.Order(StringComparer.Ordinal).Select(dimension => new DimensionSummary(dimension,
                    runs.Average(run => run.Dimensions.Single(d => d.Dimension == dimension).Score),
                    runs.Average(run => run.Dimensions.Single(d => d.Dimension == dimension).Contribution))).ToArray()));
        }
        var passed = summaries.All(summary => summary.IncompleteRuns == 0 && summary.Mean >= loaded.Configuration.FailBelow);
        var summaryReport = new SummaryReport("1.0", startedAt, loaded.Configuration.FailBelow, passed, summaries);
        await ExamReportWriter.WriteSummaryAsync(summaryReport, directory, token);
        return new ExamExecutionResult(passed ? 0 : 2, directory, summaryReport);
    }

    private static string ReserveDirectory(string root, DateTimeOffset started)
    {
        Directory.CreateDirectory(root);
        var stamp = started.UtcDateTime.ToString("yyyy-MM-dd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var temporary = Path.Combine(root, $".run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            for (var suffix = 0; ; suffix++)
            {
                var path = Path.Combine(root, suffix == 0 ? stamp : $"{stamp}-{suffix:00}");
                try
                {
                    Directory.Move(temporary, path);
                    return path;
                }
                catch (IOException) when (Directory.Exists(path)) { }
            }
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary);
        }
    }

    private static IAgentModelClient CreateModel(LoadedCandidate loaded) => new ExamModelClient(loaded);

    private sealed class ExamModelClient : IAgentModelClient, IDisposable
    {
        private readonly InMemoryContext db = new(new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase($"exam-model-{Guid.NewGuid():N}").Options);
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly OpenAiCompatibleAgentModelClient inner;

        public ExamModelClient(LoadedCandidate loaded)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GlobalSettings:" + SettingsMap.OpenAiAgentInstance] = loaded.Candidate.Endpoint,
                ["GlobalSettings:" + SettingsMap.OpenAiAgentModel] = loaded.Candidate.Model,
                ["GlobalSettings:" + SettingsMap.OpenAiAgentApiToken] = loaded.Credential
            }).Build();
            var settings = new GlobalSettingsService(db, config, null!, cache);
            inner = new OpenAiCompatibleAgentModelClient(new ExamHttpFactory(http), settings,
                NullLogger<OpenAiCompatibleAgentModelClient>.Instance);
        }

        public ValueTask<AgentModelResponse> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken) =>
            inner.CompleteAsync(request, cancellationToken);

        public void Dispose()
        {
            http.Dispose();
            cache.Dispose();
            db.Dispose();
        }
    }

    private sealed class ExamHttpFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
