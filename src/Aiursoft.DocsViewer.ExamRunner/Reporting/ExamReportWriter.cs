using System.Net;
using System.Text;
using System.Text.Json;

namespace Aiursoft.DocsViewer.ExamRunner.Reporting;

public static class ExamReportWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task WriteAsync(RepetitionReport report, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, Options), token);
        var html = new StringBuilder(HtmlStart("Exam Run"));
        html.Append("<h1>Exam Run</h1><p>Incomplete: ").Append(report.Incomplete)
            .Append(" · Candidate: ").Append(Encode(report.CandidateId))
            .Append(" · Model: ").Append(Encode(report.Model)).Append(" · Repetition: ")
            .Append(report.Repetition).Append(" · Score: ").Append(report.Total.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
            .Append("</p><h2>Scenarios</h2><table><tr><th>ID</th><th>Valid</th><th>Passed</th><th>Score</th><th>Failure</th></tr>");
        foreach (var scenario in report.Scenarios)
        {
            html.Append("<tr><td>").Append(Encode(scenario.Id)).Append("</td><td>").Append(scenario.Valid)
                .Append("</td><td>").Append(scenario.Passed).Append("</td><td>")
                .Append(scenario.Score.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(Encode(scenario.FailureKind)).Append("</td></tr>");
        }
        html.Append("</table><h2>Dimensions</h2><table><tr><th>Dimension</th><th>Score</th><th>Weight</th><th>Contribution</th></tr>");
        foreach (var dimension in report.Dimensions)
            html.Append("<tr><td>").Append(Encode(dimension.Dimension)).Append("</td><td>")
                .Append(Number(dimension.Score)).Append("</td><td>").Append(Number(dimension.Weight))
                .Append("</td><td>").Append(Number(dimension.Contribution)).Append("</td></tr>");
        html.Append("</table><h2>Assertions</h2><table><tr><th>Scenario</th><th>ID</th><th>Dimension</th><th>Matched</th></tr>");
        foreach (var scenario in report.Scenarios)
        foreach (var assertion in scenario.Assertions)
            html.Append("<tr><td>").Append(Encode(scenario.Id)).Append("</td><td>").Append(Encode(assertion.Id))
                .Append("</td><td>").Append(Encode(assertion.Dimension)).Append("</td><td>")
                .Append(assertion.Matched).Append("</td></tr>");
        html.Append("</table></body></html>");
        await File.WriteAllTextAsync(Path.Combine(directory, "report.html"), html.ToString(), token);
    }

    public static async Task WriteSummaryAsync(SummaryReport report, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(report, Options), token);
        var html = new StringBuilder(HtmlStart("Exam Summary"));
        html.Append("<h1>Exam Summary</h1><p>Started (UTC): ").Append(Encode(report.StartedAt.ToString("O")))
            .Append(" · Pass threshold: ").Append(report.FailBelow.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(" · Passed: ").Append(report.Passed).Append("</p><table><tr><th>Candidate</th><th>Model</th><th>Mean</th><th>Min</th><th>Max</th><th>Stddev</th><th>Completion</th><th>Incomplete</th><th>Invalid scenarios</th></tr>");
        foreach (var candidate in report.Candidates)
            html.Append("<tr><td>").Append(Encode(candidate.Id)).Append("</td><td>").Append(Encode(candidate.Model))
                .Append("</td><td>").Append(Number(candidate.Mean)).Append("</td><td>").Append(Number(candidate.Minimum))
                .Append("</td><td>").Append(Number(candidate.Maximum)).Append("</td><td>").Append(Number(candidate.StandardDeviation))
                .Append("</td><td>").Append(Number(candidate.CompletionRate)).Append("</td><td>")
                .Append(candidate.IncompleteRuns).Append("</td><td>").Append(candidate.InvalidScenarios).Append("</td></tr>");
        html.Append("</table><h2>Dimension averages</h2><table><tr><th>Candidate</th><th>Dimension</th><th>Mean score</th><th>Mean contribution</th></tr>");
        foreach (var candidate in report.Candidates)
        foreach (var dimension in candidate.Dimensions)
            html.Append("<tr><td>").Append(Encode(candidate.Id)).Append("</td><td>")
                .Append(Encode(dimension.Dimension)).Append("</td><td>").Append(Number(dimension.MeanScore))
                .Append("</td><td>").Append(Number(dimension.MeanContribution)).Append("</td></tr>");
        html.Append("</table></body></html>");
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.html"), html.ToString(), token);
    }

    private static string HtmlStart(string title) => "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + title +
        "</title><style>body{font-family:sans-serif;max-width:1100px;margin:2rem auto}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:.5rem}</style></head><body>";
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Number(double value) => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
}
