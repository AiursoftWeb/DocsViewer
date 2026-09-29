using Aiursoft.DocsViewer.AgentExam;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public sealed class AgentExamTests
{
    private static string Scenarios => Path.Combine(AppContext.BaseDirectory, "Scenarios", "docs-baseline-v1.json");

    [TestMethod]
    public async Task OfflineScenariosExerciseActualSearchAndPublishedAnswer()
    {
        var scenarios = ScenarioLoader.Load(Scenarios);
        Assert.AreEqual(6, scenarios.Count);
        foreach (var scenario in scenarios)
        {
            var attempt = await ExamExecutor.RunAsync(scenario);
            Assert.IsTrue(attempt.Result.Valid, $"{scenario.Id}: {attempt.Result.Error}");
            Assert.IsTrue(attempt.Result.Passed, scenario.Id);
            if (scenario.Id != "no-search")
                Assert.IsTrue(attempt.Result.Steps!.SelectMany(step => step.Tools)
                    .Any(tool => tool.Name == "search_documents" && tool.CallId is not null), scenario.Id);
        }
        var invented = await ExamExecutor.RunAsync(scenarios.Single(item => item.Id == "invented-citation"));
        Assert.IsFalse(invented.Result.Steps![0].Response.Contains("Invented answer", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MissingOrExtraReplayResponsesFailClosed()
    {
        var original = ScenarioLoader.Load(Scenarios).Single(item => item.Id == "grounded-answer");
        var missing = original.WithTurnReplies([original.Turns[0].Replay[0]]);
        var extra = original.WithTurnReplies([.. original.Turns[0].Replay, new ExamReply { Text = "extra" }]);
        Assert.IsFalse((await ExamExecutor.RunAsync(missing)).Result.Valid);
        Assert.IsFalse((await ExamExecutor.RunAsync(extra)).Result.Valid);
    }

    [TestMethod]
    public async Task AttemptsHaveIndependentCitationLabels()
    {
        var scenario = ScenarioLoader.Load(Scenarios).Single(item => item.Id == "grounded-answer");
        var first = await ExamExecutor.RunAsync(scenario);
        var second = await ExamExecutor.RunAsync(scenario);
        Assert.AreEqual("[D1]", first.Result.Steps![0].State["citations"]![0]!["label"]!.ToObject<string>());
        Assert.AreEqual("[D1]", second.Result.Steps![0].State["citations"]![0]!["label"]!.ToObject<string>());
    }

    [TestMethod]
    public void InvalidVersionAndDuplicateIdsAreRejected()
    {
        var scenario = ScenarioLoader.Load(Scenarios)[0];
        Assert.ThrowsExactly<ArgumentException>(() => ScenarioLoader.Validate(new ExamScenario
        {
            SchemaVersion = "2.0", Id = scenario.Id, Turns = scenario.Turns, Documents = scenario.Documents
        }));
        var duplicate = new ExamScenario
        {
            SchemaVersion = "1.0", Id = scenario.Id, Documents = scenario.Documents,
            Turns = [scenario.Turns[0], scenario.Turns[0]]
        };
        Assert.ThrowsExactly<ArgumentException>(() => ScenarioLoader.Validate(duplicate));
        var unknownTool = scenario.Turns[0].Replay[0].Calls[0];
        Assert.ThrowsExactly<ArgumentException>(() => ScenarioLoader.Validate(scenario.WithTurnReplies([
            new ExamReply
            {
                FinishReason = "tool_calls",
                Calls = [new ExamCall { Id = unknownTool.Id, Name = "delete_documents", Arguments = unknownTool.Arguments }]
            }, scenario.Turns[0].Replay[1]
        ])));
    }
}

internal static class ExamTestScenarioExtensions
{
    public static ExamScenario WithTurnReplies(this ExamScenario scenario, List<ExamReply> replies) => new()
    {
        SchemaVersion = scenario.SchemaVersion, Id = scenario.Id, Documents = scenario.Documents,
        Turns = [new ExamTurn
        {
            Question = scenario.Turns[0].Question, Assertions = scenario.Turns[0].Assertions, Replay = replies
        }]
    };
}
