using Aiursoft.AgentKit.Evaluator;
using Aiursoft.DocsViewer.AgentExam;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.InMemory;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Services.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length != 4 || args[0] != "--scenarios" || args[2] != "--id")
{
    Console.Error.WriteLine("Usage: dotnet run --project src/Aiursoft.DocsViewer.ExamRunner -- --scenarios <file-or-directory> --id <scenario-id>");
    return 2;
}

var endpoint = Environment.GetEnvironmentVariable("DOCSVIEWER_EXAM_ENDPOINT");
var modelName = Environment.GetEnvironmentVariable("DOCSVIEWER_EXAM_MODEL");
var token = Environment.GetEnvironmentVariable("DOCSVIEWER_EXAM_TOKEN");
if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
    string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Exam requires HTTPS endpoint, model and token environment variables.");
    return 2;
}

try
{
    var selected = ScenarioLoader.Load(args[1]).SingleOrDefault(item => item.Id == args[3]);
    if (selected is null)
    {
        Console.Error.WriteLine("Scenario ID not found.");
        return 2;
    }
    using var db = new InMemoryContext(new DbContextOptionsBuilder<InMemoryContext>()
        .UseInMemoryDatabase($"exam-config-{Guid.NewGuid():N}").Options);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["GlobalSettings:" + SettingsMap.OpenAiAgentInstance] = uri.AbsoluteUri,
        ["GlobalSettings:" + SettingsMap.OpenAiAgentModel] = modelName,
        ["GlobalSettings:" + SettingsMap.OpenAiAgentApiToken] = token
    }).Build();
    var settings = new GlobalSettingsService(db, config, null!, cache);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    var client = new OpenAiCompatibleAgentModelClient(new ExamHttpFactory(http), settings,
        NullLogger<OpenAiCompatibleAgentModelClient>.Instance);
    var attempt = await ExamExecutor.RunAsync(selected, client);
    var score = EvaluationScoreCalculator.Calculate([attempt.Result],
        new Dictionary<string, double> { ["grounding"] = .5, ["safety"] = .3, ["efficiency"] = .2 });
    Console.WriteLine($"Scenario: {selected.Id}; model: {modelName}; passed: {attempt.Result.Passed}; valid: {attempt.Result.Valid}; score: {score.Total:F1}; elapsed: {attempt.Elapsed.TotalSeconds:F1}s");
    foreach (var assertion in attempt.Result.Assertions)
        Console.WriteLine($"  {assertion.Id}: {(assertion.Matched ? "pass" : "fail")}");
    if (attempt.Result.Error is not null) Console.WriteLine($"Incomplete: {attempt.Result.Error}");
    return attempt.Result.Passed && !score.Incomplete ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Exam configuration or execution failed: {ex.GetType().Name}");
    return 2;
}

internal sealed class ExamHttpFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
