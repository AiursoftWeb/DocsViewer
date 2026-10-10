using System.Diagnostics;
using Aiursoft.AgentKit;
using Aiursoft.AgentKit.AgentRunner;
using Aiursoft.AgentKit.Evaluator;
using Aiursoft.AgentKit.Messages;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.InMemory;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Services.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DocsViewer.AgentExam;

public sealed record ExamAttempt(EvaluationCaseResult Result, TimeSpan Elapsed);

public static class ExamExecutor
{
    public static async Task<ExamAttempt> RunAsync(ExamScenario scenario, IAgentModelClient? model = null,
        CancellationToken cancellationToken = default)
    {
        ScenarioLoader.Validate(scenario);
        var scripted = model is null ? new ScriptedExamModel(scenario.Turns) : null;
        model ??= scripted!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(scenario.TimeoutSeconds));
        var clock = Stopwatch.StartNew();
        var expected = ScenarioLoader.ToCase(scenario);
        var evidence = new List<EvaluationStepEvidence>();
        using var db = new InMemoryContext(new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase($"agent-exam-{Guid.NewGuid():N}").Options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var http = new HttpClient();
        using var services = new ServiceCollection().AddLogging().AddRouting().BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GlobalSettings:" + SettingsMap.EnableEmbeddingBasedSearch] = "False"
        }).Build();
        var settings = new GlobalSettingsService(db, configuration, null!, cache);
        var vector = new DocumentVectorSearchService(db,
            new DocumentEmbeddingCache(NullLogger<DocumentEmbeddingCache>.Instance), settings,
            new ExamHttpFactory(http), NullLogger<DocumentVectorSearchService>.Instance);
        var search = new DocumentSearchAgentTool(db, vector, services.GetRequiredService<LinkGenerator>(),
            new HttpContextAccessor());
        var executor = new GroundedDocumentAnswerService(new BoundedAgentRunner(model),
            new DocumentAgentToolCatalog(search), search, new AgentRequestLimiter(), settings);
        IReadOnlyList<TranscriptMessage> history = [];
        var nextLabel = 0;
        try
        {
            foreach (var document in scenario.Documents)
                db.Documents.Add(new Document
                {
                    Title = document.Title, Category = document.Category, FilePath = document.Path,
                    Content = document.Content, SourceCulture = scenario.Culture, FileLastModified = DateTime.UnixEpoch
                });
            await db.SaveChangesAsync(deadline.Token);
            for (var index = 0; index < scenario.Turns.Count; index++)
            {
                var turn = scenario.Turns[index];
                scripted?.BeginTurn(index);
                var watch = Stopwatch.StartNew();
                var output = await executor.ExecuteTurnAsync(turn.Question, history, nextLabel, scenario.Culture,
                    scenario.PathBase, null, null, deadline.Token);
                if (output.Run is null) throw new InvalidOperationException("Document turn produced no agent run.");
                scripted?.EndTurn();
                var snapshot = new JObject
                {
                    ["status"] = output.Answer.Status.ToString(),
                    ["sufficientEvidence"] = output.Answer.SufficientEvidence,
                    ["citations"] = JArray.FromObject(output.Answer.Citations.Select(c => new
                    {
                        label = c.Label, path = c.Path, url = c.Url
                    }).ToArray()),
                    ["documents"] = JArray.FromObject(db.Documents.AsNoTracking().OrderBy(d => d.FilePath)
                        .Select(d => new { path = d.FilePath, title = d.Title }).ToArray())
                };
                var adapted = AgentRunEvidenceAdapter.ToStepEvidence(
                    new EvaluationStepIdentity(index, "document-user", scenario.Id), output.Run, snapshot, watch.Elapsed);
                evidence.Add(adapted with { Response = output.Answer.Answer });
                history = output.Transcript;
                nextLabel = output.NextLabel;
            }
            if (scripted is not null && !scripted.Complete)
                throw new InvalidOperationException("Replay was not completely consumed.");
            return new ExamAttempt(new AssertionEvaluator().Evaluate(expected, new EvaluationEvidence(evidence)), clock.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex is OperationCanceledException ? "Timeout or cancellation" : ex.GetType().Name;
            return new ExamAttempt(new AssertionEvaluator().Evaluate(expected,
                new EvaluationEvidence(evidence, false, reason)), clock.Elapsed);
        }
    }

    private sealed class ExamHttpFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
