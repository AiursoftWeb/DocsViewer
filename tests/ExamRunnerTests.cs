using System.Text.Json;
using Aiursoft.DocsViewer.AgentExam;
using Aiursoft.DocsViewer.ExamRunner.Configuration;
using Aiursoft.DocsViewer.ExamRunner.Execution;
using Aiursoft.DocsViewer.ExamRunner.Reporting;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public sealed class ExamRunnerTests
{
    private static string Scenarios => Path.Combine(AppContext.BaseDirectory, "Scenarios", "docs-baseline-v1.json");
    private static string LiveScenarios => Path.Combine(AppContext.BaseDirectory, "Scenarios", "docs-live-v1.json");

    [TestMethod]
    public void LiveBaselineLoadsWithoutReplayResponses()
    {
        var scenarios = ScenarioLoader.Load(LiveScenarios);
        Assert.AreEqual(3, scenarios.Count);
        Assert.IsTrue(scenarios.All(scenario => scenario.Turns.All(turn => turn.Replay.Count == 0)));
        Assert.IsTrue(scenarios.All(scenario => scenario.Turns.SelectMany(turn => turn.Assertions)
            .Any(assertion => assertion.Dimension == "safety")));
    }

    [TestMethod]
    public async Task ConfigurationRejectsMissingCredentialsAndEscapingPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"docs-exam-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "config.json");
            File.Copy(Scenarios, Path.Combine(root, "scenario.json"));
            await File.WriteAllTextAsync(path, Config("missing-exam-token-" + Guid.NewGuid().ToString("N")));
            var missingCredential = await Assert.ThrowsAsync<InvalidOperationException>(() => ExamConfigurationLoader.LoadAsync(path));
            StringAssert.Contains(missingCredential.Message, "credential");
            Assert.ThrowsExactly<InvalidOperationException>(() => ExamConfigurationLoader.ResolvePath(root, "../escape"));
            Assert.ThrowsExactly<InvalidOperationException>(() => ExamConfigurationLoader.ResolvePath(root, "/absolute"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ConfigurationLoadsRelativePathsAndRejectsUnknownProperties()
    {
        var root = Path.Combine(Path.GetTempPath(), $"docs-exam-config-valid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.Copy(Scenarios, Path.Combine(root, "scenario.json"));
            var configPath = Path.Combine(root, "config.json");
            await File.WriteAllTextAsync(configPath, Config("unused")
                .Replace("\"mode\":\"bearer\"", "\"mode\":\"none\"")
                .Replace(",\"environmentVariable\":\"unused\"", ""));
            var loaded = await ExamConfigurationLoader.LoadAsync(configPath);
            Assert.AreEqual(Path.Combine(root, "scenario.json"), loaded.ScenarioPaths.Single());
            Assert.AreEqual(root, Path.GetDirectoryName(loaded.OutputDirectory));
            await File.WriteAllTextAsync(configPath, (await File.ReadAllTextAsync(configPath))
                .Replace("\"schemaVersion\"", "\"unknownField\":true,\"schemaVersion\""));
            await Assert.ThrowsAsync<JsonException>(() => ExamConfigurationLoader.LoadAsync(configPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MultiCandidateRepetitionsProduceSafeReportsAndSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"docs-exam-reports-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scenarios = ScenarioLoader.Load(Scenarios);
            var selected = scenarios.Single(item => item.Id == "grounded-answer");
            var fixture = Path.Combine(root, "scenario.json");
            await File.WriteAllTextAsync(fixture, Newtonsoft.Json.JsonConvert.SerializeObject(selected));
            var loaded = new LoadedExamConfiguration(
                new ExamConfiguration { SchemaVersion = "1.0", Scenarios = ["scenario.json"], FailBelow = 100,
                    Candidates = [Candidate("first"), Candidate("second")] },
                [fixture], Path.Combine(root, "reports"),
                [new LoadedCandidate(Candidate("first"), "secret-canary"),
                    new LoadedCandidate(Candidate("second"), "secret-canary")]);
            var executions = 0;
            var orchestrator = new ExamOrchestrator(_ => new ScriptedExamModel(selected.Turns),
                async (scenario, model, token) =>
                {
                    executions++;
                    var result = await ExamExecutor.RunAsync(scenario, model, token);
                    return result;
                });
            var result = await orchestrator.RunAsync(loaded);
            Assert.AreEqual(0, result.ExitCode);
            Assert.AreEqual(100d, result.Summary.FailBelow);
            Assert.AreEqual("1.0", result.Summary.SchemaVersion);
            Assert.AreEqual(4, executions);
            Assert.AreEqual(2, result.Summary.Candidates.Count);
            foreach (var candidate in result.Summary.Candidates)
            {
                Assert.AreEqual(2, candidate.Repetitions);
                Assert.AreEqual(100d, candidate.Mean);
                Assert.AreEqual(100d, candidate.Minimum);
                Assert.AreEqual(100d, candidate.Maximum);
                Assert.AreEqual(0d, candidate.StandardDeviation);
                Assert.AreEqual(1d, candidate.CompletionRate);
                Assert.IsTrue(File.Exists(Path.Combine(result.OutputDirectory, candidate.Id, "repetition-2", "report.html")));
            }
            var reportJson = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "first", "repetition-1", "report.json"));
            var report = JsonSerializer.Deserialize<RepetitionReport>(reportJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.AreEqual("1.0", report.SchemaVersion);
            Assert.IsTrue(report.Scenarios.Single().ElapsedSeconds >= 0);
            var summaryHtml = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "summary.html"));
            var summaryJson = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "summary.json"));
            Assert.IsFalse(reportJson.Contains("secret-canary", StringComparison.Ordinal));
            Assert.IsFalse(reportJson.Contains("release switch", StringComparison.Ordinal));
            Assert.IsFalse(summaryHtml.Contains("secret-canary", StringComparison.Ordinal));
            Assert.IsFalse(summaryJson.Contains("secret-canary", StringComparison.Ordinal));
            Assert.IsFalse(summaryJson.Contains("release switch", StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(Path.Combine(result.OutputDirectory, "summary.json")));
            using var parsed = JsonDocument.Parse(reportJson);
            Assert.AreEqual("first", parsed.RootElement.GetProperty("candidateId").GetString());
            var another = await orchestrator.RunAsync(loaded);
            Assert.AreNotEqual(result.OutputDirectory, another.OutputDirectory);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task IncompleteRunHasZeroContributionAndFailsThreshold()
    {
        var root = Path.Combine(Path.GetTempPath(), $"docs-exam-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scenario = ScenarioLoader.Load(Scenarios).Single(item => item.Id == "grounded-answer");
            var fixture = Path.Combine(root, "scenario.json");
            await File.WriteAllTextAsync(fixture, Newtonsoft.Json.JsonConvert.SerializeObject(scenario));
            var candidate = Candidate("candidate");
            var loaded = new LoadedExamConfiguration(
                new ExamConfiguration { SchemaVersion = "1.0", Scenarios = ["scenario.json"], FailBelow = 50,
                    Candidates = [candidate] }, [fixture], Path.Combine(root, "reports"),
                [new LoadedCandidate(candidate, null)]);
            var index = 0;
            var orchestrator = new ExamOrchestrator(_ => new ScriptedExamModel(scenario.Turns),
                async (item, model, token) =>
                {
                    if (++index == 2) throw new InvalidOperationException("sensitive-server-response");
                    return await ExamExecutor.RunAsync(item, model, token);
                });
            var result = await orchestrator.RunAsync(loaded);
            var summary = result.Summary.Candidates.Single();
            Assert.AreEqual(2, result.ExitCode);
            Assert.AreEqual(50d, summary.Mean);
            Assert.AreEqual(0d, summary.Minimum);
            Assert.AreEqual(100d, summary.Maximum);
            Assert.AreEqual(50d, summary.StandardDeviation);
            Assert.AreEqual(.5d, summary.CompletionRate);
            Assert.AreEqual(1, summary.IncompleteRuns);
            var report = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "candidate", "repetition-2", "report.json"));
            Assert.IsFalse(report.Contains("sensitive-server-response", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ScoreBelowThresholdFailsWithoutIncompleteAttempts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"docs-exam-threshold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scenario = ScenarioLoader.Load(Scenarios).Single(item => item.Id == "grounded-answer");
            var fixture = Path.Combine(root, "scenario.json");
            await File.WriteAllTextAsync(fixture, Newtonsoft.Json.JsonConvert.SerializeObject(scenario));
            var candidate = Candidate("candidate");
            var loaded = new LoadedExamConfiguration(
                new ExamConfiguration { SchemaVersion = "1.0", Scenarios = ["scenario.json"], FailBelow = 100,
                    Candidates = [candidate] }, [fixture], Path.Combine(root, "reports"),
                [new LoadedCandidate(candidate, null)]);
            var orchestrator = new ExamOrchestrator(_ => new ScriptedExamModel(scenario.Turns),
                async (item, model, token) =>
                {
                    var attempt = await ExamExecutor.RunAsync(item, model, token);
                    return attempt with { Result = attempt.Result with { Assertions =
                        attempt.Result.Assertions.Select(a => a with { Matched = false, Earned = 0 }).ToArray() } };
                });
            var result = await orchestrator.RunAsync(loaded);
            Assert.AreEqual(2, result.ExitCode);
            Assert.AreEqual(0, result.Summary.Candidates.Single().IncompleteRuns);
            Assert.AreEqual(0d, result.Summary.Candidates.Single().Mean);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CallerCancellationStopsBeforeWritingSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"docs-exam-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scenario = ScenarioLoader.Load(Scenarios).Single(item => item.Id == "grounded-answer");
            var fixture = Path.Combine(root, "scenario.json");
            await File.WriteAllTextAsync(fixture, Newtonsoft.Json.JsonConvert.SerializeObject(scenario));
            var candidate = Candidate("candidate");
            var loaded = new LoadedExamConfiguration(
                new ExamConfiguration { SchemaVersion = "1.0", Scenarios = ["scenario.json"],
                    Candidates = [candidate] }, [fixture], Path.Combine(root, "reports"),
                [new LoadedCandidate(candidate, null)]);
            var orchestrator = new ExamOrchestrator(_ => new ScriptedExamModel(scenario.Turns));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => orchestrator.RunAsync(loaded, cancellation.Token));
            }
            Assert.IsFalse(Directory.GetFiles(root, "summary.json", SearchOption.AllDirectories).Any());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task HtmlEncodesExternalLabels()
    {
        var root = Path.Combine(Path.GetTempPath(), $"docs-exam-html-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await ExamReportWriter.WriteAsync(new RepetitionReport("1.0", "<script>", "<b>", 1, 0, false, [],
                [new ScenarioReport("<img>", true, false, 0, 0, null,
                    [new AssertionReport("<x>", "<y>", false)])]), root, CancellationToken.None);
            var html = await File.ReadAllTextAsync(Path.Combine(root, "report.html"));
            Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("<img>", StringComparison.Ordinal));
            Assert.IsTrue(html.Contains("&lt;script&gt;", StringComparison.Ordinal));
            Assert.IsTrue(html.Contains("&lt;img&gt;", StringComparison.Ordinal));
            Assert.IsTrue(html.Contains("&lt;x&gt;", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    private static CandidateConfiguration Candidate(string id) => new()
    {
        Id = id, Endpoint = "https://example.invalid/v1/chat/completions", Model = "test-model", Repetitions = 2,
        Authentication = new CandidateAuthentication { Mode = "none" }
    };

    private static string Config(string name) => JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0", scenarios = new[] { "scenario.json" },
        candidates = new[] { new
        {
            id = "candidate", endpoint = "https://example.invalid/v1/chat/completions", model = "test",
            authentication = new { mode = "bearer", environmentVariable = name }
        } }
    });
}
