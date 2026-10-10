using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aiursoft.DocsViewer.ExamRunner.Configuration;

public static partial class ExamConfigurationLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CandidateIdPattern();

    public static async Task<LoadedExamConfiguration> LoadAsync(string path, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var configurationPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(configurationPath)!;
        var json = await File.ReadAllTextAsync(configurationPath, token);
        var configuration = JsonSerializer.Deserialize<ExamConfiguration>(json, Options) ??
            throw new InvalidOperationException("Exam configuration is empty.");
        if (configuration.SchemaVersion != "1.0" || configuration.Scenarios is not { Length: > 0 } ||
            configuration.Candidates is not { Length: > 0 } || !double.IsFinite(configuration.FailBelow) ||
            configuration.FailBelow is < 0 or > 100)
            throw new InvalidOperationException("Invalid exam configuration.");

        var paths = configuration.Scenarios.Select(value => ResolvePath(directory, value)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        foreach (var scenarioPath in paths)
            if (!File.Exists(scenarioPath) && !Directory.Exists(scenarioPath))
                throw new InvalidOperationException("Configured scenario path does not exist.");
        var output = ResolvePath(directory, configuration.OutputDirectory);
        if (paths.Any(scenarioPath => scenarioPath == output ||
            scenarioPath.StartsWith(output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Directory.Exists(scenarioPath) && output.StartsWith(scenarioPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new InvalidOperationException("Report output must be separate from scenario paths.");
        var candidates = new List<LoadedCandidate>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in configuration.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id) || !CandidateIdPattern().IsMatch(candidate.Id) ||
                !ids.Add(candidate.Id) || string.IsNullOrWhiteSpace(candidate.Model) || candidate.Repetitions is < 1 or > 20 ||
                !Uri.TryCreate(candidate.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.Fragment.Length > 0)
                throw new InvalidOperationException("Invalid exam candidate configuration.");
            string? credential;
            switch (candidate.Authentication.Mode)
            {
                case "none" when candidate.Authentication.EnvironmentVariable is null:
                    credential = null;
                    break;
                case "bearer" when !string.IsNullOrWhiteSpace(candidate.Authentication.EnvironmentVariable):
                    credential = Environment.GetEnvironmentVariable(candidate.Authentication.EnvironmentVariable);
                    if (string.IsNullOrWhiteSpace(credential))
                        throw new InvalidOperationException("Exam candidate credential environment variable is missing.");
                    break;
                default:
                    throw new InvalidOperationException("Unsupported exam authentication configuration.");
            }
            candidates.Add(new LoadedCandidate(candidate, credential));
        }
        return new LoadedExamConfiguration(configuration, paths, output, candidates);
    }

    public static string ResolvePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Exam paths must be relative to the configuration directory.");
        var basePath = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(basePath, relative));
        if (!result.StartsWith(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) && result != basePath)
            throw new InvalidOperationException("Exam path escapes configuration directory.");
        var current = basePath;
        foreach (var part in Path.GetRelativePath(basePath, result).Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Exam paths cannot traverse symbolic links.");
        }
        return result;
    }
}
