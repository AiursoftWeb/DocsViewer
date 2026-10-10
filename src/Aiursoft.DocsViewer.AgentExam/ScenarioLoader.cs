using Aiursoft.AgentKit.Evaluator;
using Aiursoft.DocsViewer.Services.Agents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DocsViewer.AgentExam;

public static class ScenarioLoader
{
    public static IReadOnlyList<ExamScenario> Load(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.json").OrderBy(file => file, StringComparer.Ordinal).ToArray()
            : [Path.GetFullPath(path)];
        if (files.Length == 0) throw new ArgumentException("No scenario files were found.", nameof(path));
        var scenarios = new List<ExamScenario>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            using var fileReader = new StreamReader(file);
            using (var reader = new JsonTextReader(fileReader))
            {
                reader.MaxDepth = 32;
                var root = JToken.Load(reader);
                foreach (var item in root is JArray array ? array.Children().AsEnumerable() : new[] { root })
                {
                    var scenario = item.ToObject<ExamScenario>() ?? throw new ArgumentException("Invalid scenario.", nameof(path));
                    Validate(scenario);
                    if (!ids.Add(scenario.Id)) throw new ArgumentException("Duplicate scenario ID.", nameof(path));
                    scenarios.Add(scenario);
                }
            }
        }
        return scenarios;
    }

    public static EvaluationCase ToCase(ExamScenario scenario) => new(scenario.Id, scenario.Weight,
        scenario.Turns.Select((turn, index) => new EvaluationStep(index, "document-user", scenario.Id,
            new EvaluationExpectation(turn.Assertions.Select(assertion => new EvaluationAssertion(
                assertion.Id, assertion.Kind, assertion.Dimension, assertion.Points, assertion.Penalty,
                assertion.Required, assertion.HardFail, assertion.Match.DeepClone())).ToArray()))).ToArray());

    public static void Validate(ExamScenario scenario)
    {
        if (scenario.SchemaVersion != "1.0" || string.IsNullOrWhiteSpace(scenario.Id) ||
            scenario.TimeoutSeconds is < 1 or > 300 || scenario.Turns is not { Count: > 0 } ||
            scenario.Turns.Count > 20 || scenario.Documents.Count > 30 ||
            !System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.AllCultures).Any(c => c.Name == scenario.Culture))
            throw new ArgumentException("Invalid scenario metadata.", nameof(scenario));
        if (scenario.PathBase.Length > 100 || scenario.PathBase.Contains("..", StringComparison.Ordinal) ||
            scenario.PathBase.Length > 0 && !scenario.PathBase.StartsWith('/'))
            throw new ArgumentException("Invalid path base.", nameof(scenario));
        if (scenario.Turns.Any(turn => turn.Replay is { Count: > 0 }) &&
            scenario.Turns.Any(turn => turn.Replay is not { Count: > 0 }))
            throw new ArgumentException("Replay must be provided for every turn or none.", nameof(scenario));
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in scenario.Documents)
        {
            if (string.IsNullOrWhiteSpace(document.Title) || string.IsNullOrWhiteSpace(document.Category) ||
                string.IsNullOrWhiteSpace(document.Content) || document.Content.Length > 20_000 ||
                string.IsNullOrWhiteSpace(document.Path) || Path.IsPathRooted(document.Path) ||
                document.Path.Split('/').Any(part => part is ".." or "." or "") || !paths.Add(document.Path))
                throw new ArgumentException("Invalid document seed.", nameof(scenario));
        }
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in scenario.Turns)
        {
            if (string.IsNullOrWhiteSpace(turn.Question) || turn.Question.Length > 2000 || turn.Assertions is not { Count: > 0 } ||
                turn.Replay is null || turn.Replay.Count > 20)
                throw new ArgumentException("Invalid turn.", nameof(scenario));
            foreach (var reply in turn.Replay)
            {
                if (reply.Calls is null) throw new ArgumentException("Invalid scripted response.", nameof(scenario));
                if (reply.FinishReason is not ("stop" or "tool_calls" or "length" or "refusal") ||
                    (reply.FinishReason == "tool_calls") != (reply.Calls.Count > 0))
                    throw new ArgumentException("Invalid scripted response.", nameof(scenario));
                foreach (var call in reply.Calls)
                {
                    if (string.IsNullOrWhiteSpace(call.Id) || !callIds.Add(call.Id) ||
                        call.Name != DocumentSearchAgentTool.Name ||
                        call.Arguments.Value<string>("query") is not { Length: > 0 and <= 500 })
                        throw new ArgumentException("Invalid scripted tool call.", nameof(scenario));
                }
            }
            foreach (var assertion in turn.Assertions.Where(a => a.Kind is EvaluationAssertionKinds.Tool or EvaluationAssertionKinds.ForbidTool))
                if (assertion.Match is not JObject match || match.Value<string>("name") != DocumentSearchAgentTool.Name)
                    throw new ArgumentException("Unsupported tool assertion.", nameof(scenario));
        }
        foreach (var assertion in scenario.Turns.SelectMany(turn => turn.Assertions))
            ValidateMatchOperators(assertion.Match);
        EvaluationDefinitionValidator.Validate(ToCase(scenario));
    }

    private static void ValidateMatchOperators(JToken token)
    {
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                if (property.Name.StartsWith('$') && !JsonMatcher.SupportedOperators.Contains(property.Name))
                    throw new ArgumentException("Unsupported JSON match operator.");
                if (property.Name == "$var")
                    throw new ArgumentException("Variable matching is not configured for DocsViewer exams.");
                ValidateMatchOperators(property.Value);
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array) ValidateMatchOperators(item);
        }
    }
}
