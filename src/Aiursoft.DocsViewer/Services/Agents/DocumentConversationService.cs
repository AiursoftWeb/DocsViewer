using System.Globalization;
using Aiursoft.AgentKit;
using Aiursoft.AgentKit.Messages;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DocsViewer.Services.Agents;

// ReSharper disable NotAccessedPositionalProperty.Global
public sealed record DocumentProcessEvent(
    long Sequence,
    int Iteration,
    string Kind,
    string Status,
    string? Text = null,
    string? ToolName = null,
    JToken? Arguments = null,
    int? ResultCount = null,
    IReadOnlyList<ConversationCitation>? Citations = null,
    string? ToolCallId = null)
{
    public const string AssistantMessage = "AssistantMessage";
    public const string DocumentationSearch = "DocumentationSearch";
    public const string ToolCall = "ToolCall";
    public const string ToolExecution = "ToolExecution";
    public const string Proposed = "Proposed";
    public const string Started = "Started";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Deferred = "Deferred";

    public DocumentProcessEvent(string kind, string status)
        : this(0, 0, kind, status)
    {
    }

    public DocumentProcessEvent DeepCopy() => this with
    {
        Arguments = Arguments?.DeepClone(),
        Citations = Citations?.ToArray()
    };
}
// ReSharper restore NotAccessedPositionalProperty.Global

public sealed record DocumentTurnResult(
    GroundedDocumentAnswer Answer,
    IReadOnlyList<TranscriptMessage> Transcript,
    int NextLabel,
    IReadOnlyList<DocumentProcessEvent>? ProcessEvents = null,
    AgentRunResult? Run = null);

// ReSharper disable NotAccessedPositionalProperty.Global
public sealed record ConversationCitation(string Label, string Title, string Url);
// ReSharper restore NotAccessedPositionalProperty.Global
public sealed record ConversationMessage(
    string Role,
    string Content,
    IReadOnlyList<ConversationCitation> Citations,
    IReadOnlyList<DocumentProcessEvent>? ProcessEvents = null);

// ReSharper disable NotAccessedPositionalProperty.Global
public sealed record ConversationSnapshot(
    Guid ConversationId,
    string State,
    IReadOnlyList<ConversationMessage> Messages,
    IReadOnlyList<object> PendingAdvice,
    string? ErrorMessage,
    long Version,
    IReadOnlyList<DocumentProcessEvent>? ActiveProcessEvents = null,
    IReadOnlyList<AgentDiagnosticEvent>? MetaEvents = null);
// ReSharper restore NotAccessedPositionalProperty.Global

public sealed record ConversationAdmission(Guid? ConversationId, string? Error);

/// <summary>Process-local, owned conversations. Queue and worker lifetime are independent of HTTP requests.</summary>
public sealed class DocumentConversationService : IDisposable
{
    private const int MaxHistoryCharacters = 100_000;
    private readonly object sync = new();
    private readonly Dictionary<Guid, Conversation> conversations = [];
    private readonly IDocumentConversationQueue queue;
    private readonly IServiceScopeFactory scopes;
    private readonly AgentRequestLimiter limiter;
    private readonly ILogger<DocumentConversationService> logger;
    private readonly TimeProvider clock;
    private readonly CancellationTokenRegistration stoppingRegistration;
    private bool stopping;
    private int lane;

    public DocumentConversationService(
        IDocumentConversationQueue queue,
        IServiceScopeFactory scopes,
        AgentRequestLimiter limiter,
        IHostApplicationLifetime lifetime,
        TimeProvider clock,
        ILogger<DocumentConversationService> logger)
    {
        this.queue = queue;
        this.scopes = scopes;
        this.limiter = limiter;
        this.clock = clock;
        this.logger = logger;
        stoppingRegistration = lifetime.ApplicationStopping.Register(Stop);
    }

    public async Task<ConversationAdmission> SendAsync(
        string owner,
        string message,
        Guid? id,
        string culture,
        string pathBase,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(message) || message.Length > 2000)
            return new(null, "InvalidMessage");
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (stopping) return new(null, "Stopping");
            Cleanup();
            if (id.HasValue && (!conversations.TryGetValue(id.Value, out var current) || current.Owner != owner))
                return new(null, "NotFound");
        }

        var admission = await limiter.AcquireAsync(owner, cancellationToken);
        if (admission.Lease is null)
            return new(null, admission.Status == AgentLimitStatus.RateLimited ? "RateLimited" : "Busy");
        var lease = admission.Lease;

        lock (sync)
        {
            if (stopping) { lease.Dispose(); return new(null, "Stopping"); }
            Cleanup();
            Conversation conversation;
            if (id.HasValue)
            {
                if (!conversations.TryGetValue(id.Value, out conversation!) || conversation.Owner != owner)
                { lease.Dispose(); return new(null, "NotFound"); }
                if (conversation.Active) { lease.Dispose(); return new(null, "Busy"); }
            }
            else
            {
                if (conversations.Count >= 200 || conversations.Values.Count(x => x.Owner == owner) >= 5)
                { lease.Dispose(); return new(null, "Capacity"); }
                conversation = new Conversation(owner, culture, pathBase, clock.GetUtcNow());
                conversations.Add(conversation.Id, conversation);
            }

            if (conversation.Turns >= 20 || HistorySize(conversation.History) >= MaxHistoryCharacters)
            { lease.Dispose(); return new(null, "Capacity"); }

            conversation.Active = true;
            conversation.State = "Thinking";
            conversation.Error = null;
            conversation.Generation++;
            conversation.Version++;
            conversation.Updated = clock.GetUtcNow();
            var run = new Run(lease);
            conversation.Run = run;
            run.Cancellation.CancelAfter(TimeSpan.FromMinutes(5));
            conversation.Messages.Add(new ConversationMessage("user", message.Trim(), []));
            var generation = conversation.Generation;
            try
            {
                queue.Enqueue((lane++ & int.MaxValue) % 4, $"AgentTurn-{conversation.Id}-{generation}",
                    () => ExecuteAsync(conversation, generation, message.Trim(), run));
            }
            catch
            {
                conversation.Messages.RemoveAt(conversation.Messages.Count - 1);
                conversation.Active = false;
                conversation.State = "Error";
                conversation.Error = "QueueFailure";
                Finish(conversation, generation, run);
                if (!id.HasValue) conversations.Remove(conversation.Id);
                return new(null, "QueueFailure");
            }

            return new(conversation.Id, null);
        }
    }

    public ConversationSnapshot? Status(string owner, Guid id)
    {
        lock (sync)
        {
            Cleanup();
            if (!conversations.TryGetValue(id, out var conversation) || conversation.Owner != owner) return null;
            return new ConversationSnapshot(
                conversation.Id,
                conversation.State,
                conversation.Messages.Select(CloneMessage).ToArray(),
                [],
                conversation.Error,
                conversation.Version,
                conversation.ActiveProcessEvents.Select(process => process.DeepCopy()).ToArray(),
                conversation.DiagnosticEvents.Select(item => item.DeepCopy()).ToArray());
        }
    }

    public bool Cancel(string owner, Guid id)
    {
        lock (sync)
        {
            Cleanup();
            if (!conversations.TryGetValue(id, out var conversation) || conversation.Owner != owner) return false;
            if (!conversation.Active) return true;
            conversation.State = "Cancelling";
            conversation.Version++;
            var run = conversation.Run!;
            run.Cancellation.Cancel();
            if (!run.Started) Finish(conversation, conversation.Generation, run);
            return true;
        }
    }

    private async Task ExecuteAsync(Conversation conversation, long generation, string message, Run run)
    {
        lock (sync)
        {
            if (run.Finished) return;
            run.Started = true;
        }

        try
        {
            run.Cancellation.Token.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();
            var oldCulture = CultureInfo.CurrentCulture;
            var oldUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(conversation.Culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(conversation.Culture);
                var turn = await scope.ServiceProvider.GetRequiredService<IDocumentTurnExecutor>().ExecuteTurnAsync(
                    message,
                    conversation.History,
                    conversation.NextLabel,
                    conversation.Culture,
                    conversation.PathBase,
                    (agentEvent, token) => PublishProgressAsync(conversation, generation, run, agentEvent, token),
                    (processEvent, token) => PublishProcessAsync(conversation, generation, run, processEvent, token),
                    run.Cancellation.Token);
                lock (sync)
                {
                    if (generation != conversation.Generation || run.Cancellation.IsCancellationRequested) return;
                    AddDiagnosticRun(conversation, generation, turn.Run);
                    if (HistorySize(turn.Transcript) > MaxHistoryCharacters || turn.NextLabel > 400)
                    {
                        conversation.State = "Error";
                        conversation.Error = "Capacity";
                        conversation.ActiveProcessEvents.Clear();
                    }
                    else
                    {
                        conversation.History = turn.Transcript.Select(item => item.DeepCopy()).ToArray();
                        conversation.NextLabel = turn.NextLabel;
                        if (turn.Answer.Status is DocumentAnswerStatus.ModelFailure or DocumentAnswerStatus.MaxIterations)
                        {
                            conversation.State = "Error";
                            conversation.Error = turn.Answer.Status.ToString();
                            conversation.ActiveProcessEvents.Clear();
                        }
                        else
                        {
                            foreach (var processEvent in turn.ProcessEvents ?? [])
                            {
                                if (conversation.ActiveProcessEvents.Count >= 20 || !IsPublicSearchSummary(processEvent))
                                    continue;
                                conversation.ActiveProcessEvents.Add(WithConversationSequence(conversation, processEvent));
                            }
                            conversation.Messages.Add(new ConversationMessage(
                                "assistant",
                                turn.Answer.Answer,
                                turn.Answer.Citations.Select(citation => new ConversationCitation(citation.Label, citation.Title, citation.Url)).ToArray(),
                                conversation.ActiveProcessEvents.Select(process => process.DeepCopy()).ToArray()));
                            conversation.ActiveProcessEvents.Clear();
                            conversation.State = "Completed";
                        }
                    }
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = oldCulture;
                CultureInfo.CurrentUICulture = oldUiCulture;
            }
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            // Cancellation wins; Finish publishes the terminal state after clearing active events.
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Document agent turn execution failed. Conversation: {ConversationId}; generation: {Generation}; error type: {ErrorType}.",
                conversation.Id,
                generation,
                ex.GetType().Name);
            lock (sync)
            {
                if (generation == conversation.Generation)
                {
                    conversation.State = "Error";
                    conversation.Error = "ExecutionFailure";
                    AddDiagnosticEvent(conversation, generation, new AgentRunEvent(0, 0, AgentRunEventKind.RunCompleted,
                        RunOutcome: AgentRunOutcome.ModelFailure));
                }
            }
        }
        finally
        {
            lock (sync) Finish(conversation, generation, run);
        }
    }

    private ValueTask PublishProgressAsync(Conversation conversation, long generation, Run run, AgentRunEvent agentEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (generation != conversation.Generation || !ReferenceEquals(conversation.Run, run) || run.Finished || run.Cancellation.IsCancellationRequested)
                return ValueTask.CompletedTask;

            AddDiagnosticEvent(conversation, generation, agentEvent);
            var mapped = MapPublicEvent(agentEvent);
            if (mapped is null) return ValueTask.CompletedTask;
            if (conversation.ActiveProcessEvents.Count >= 20) return ValueTask.CompletedTask;
            conversation.ActiveProcessEvents.Add(WithConversationSequence(conversation, mapped));
            conversation.Updated = clock.GetUtcNow();
            conversation.Version++;
        }
        return ValueTask.CompletedTask;
    }

    private ValueTask PublishProcessAsync(
        Conversation conversation,
        long generation,
        Run run,
        DocumentProcessEvent processEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (generation != conversation.Generation || !ReferenceEquals(conversation.Run, run) ||
                run.Finished || run.Cancellation.IsCancellationRequested)
                return ValueTask.CompletedTask;

            if (conversation.ActiveProcessEvents.Count >= 20 || !IsPublicSearchSummary(processEvent))
                return ValueTask.CompletedTask;

            conversation.ActiveProcessEvents.Add(WithConversationSequence(conversation, processEvent));
            conversation.Updated = clock.GetUtcNow();
            conversation.Version++;
        }
        return ValueTask.CompletedTask;
    }

    private static void AddDiagnosticEvent(Conversation conversation, long generation, AgentRunEvent value)
    {
        if (conversation.DiagnosticEvents.Count >= 100) return;
        conversation.DiagnosticEvents.Add(new AgentDiagnosticEvent(
            value.Sequence,
            generation,
            value.Iteration,
            value.Kind.ToString(),
            Text: value.Text is null ? null : Limit(value.Text, 2_000),
            ToolCallId: value.ToolCallId ?? value.ToolCall?.Id,
            ToolName: value.ToolName ?? value.ToolCall?.Name,
            Arguments: value.ToolCall is null ? null : BoundedJson(value.ToolCall.Arguments),
            ToolOutcome: value.ToolOutcome,
            RunOutcome: value.RunOutcome,
            FailureCategory: value.RunOutcome is { } outcome && outcome != AgentRunOutcome.Completed ? outcome.ToString() : null));
    }

    private static void AddDiagnosticRun(Conversation conversation, long generation, AgentRunResult? run)
    {
        if (run is null) return;
        foreach (var result in run.Results)
        {
            if (conversation.DiagnosticEvents.Count >= 100) return;
            conversation.DiagnosticEvents.Add(new AgentDiagnosticEvent(
                0,
                generation,
                run.Iterations,
                "ToolResult",
                ToolCallId: result.CallId,
                ToolName: result.Name,
                Output: result.Output is { } output ? BoundedJson(output) : null,
                ToolOutcome: result.Outcome));
        }
        if (conversation.DiagnosticEvents.Count >= 100) return;
        conversation.DiagnosticEvents.Add(new AgentDiagnosticEvent(
            0,
            generation,
            run.Iterations,
            "RunResult",
            RunOutcome: run.Outcome,
            FailureCategory: run.Outcome == AgentRunOutcome.Completed ? null : run.Outcome.ToString()));
    }

    private static JToken BoundedJson(JToken value)
    {
        var raw = value.ToString(Newtonsoft.Json.Formatting.None);
        return raw.Length <= 10_000 ? value.DeepClone() : new JValue(raw[..10_000]);
    }

    private static DocumentProcessEvent WithConversationSequence(
        Conversation conversation,
        DocumentProcessEvent processEvent) => processEvent with
        {
            Sequence = ++conversation.NextProcessSequence
        };

    private static bool IsPublicSearchSummary(DocumentProcessEvent processEvent) =>
        processEvent.Kind == DocumentProcessEvent.ToolExecution &&
        processEvent.Status == DocumentProcessEvent.Succeeded &&
        processEvent.ToolName == DocumentSearchAgentTool.Name &&
        processEvent.ResultCount is >= 0 and <= 5 &&
        processEvent.Citations is { Count: <= 5 };

    private static DocumentProcessEvent? MapPublicEvent(AgentRunEvent value)
    {
        return value.Kind switch
        {
            AgentRunEventKind.ToolCallProposed when value.ToolName == DocumentSearchAgentTool.Name && value.ToolCall is not null &&
                TryGetSearchArguments(value.ToolCall.Arguments, out var arguments) =>
                new DocumentProcessEvent(value.Sequence, value.Iteration, DocumentProcessEvent.ToolCall, DocumentProcessEvent.Proposed,
                    ToolName: DocumentSearchAgentTool.Name, Arguments: arguments,
                    ToolCallId: value.ToolCallId ?? value.ToolCall.Id),
            AgentRunEventKind.ToolExecutionStarted when value.ToolName == DocumentSearchAgentTool.Name =>
                new DocumentProcessEvent(value.Sequence, value.Iteration, DocumentProcessEvent.ToolExecution, DocumentProcessEvent.Started,
                    ToolName: DocumentSearchAgentTool.Name, ToolCallId: value.ToolCallId),
            AgentRunEventKind.ToolExecutionCompleted when value.ToolName == DocumentSearchAgentTool.Name &&
                value.ToolOutcome is not ToolOutcome.Succeeded && value.ToolOutcome is not null =>
                new DocumentProcessEvent(value.Sequence, value.Iteration, DocumentProcessEvent.ToolExecution,
                    value.ToolOutcome == ToolOutcome.Deferred ? DocumentProcessEvent.Deferred : DocumentProcessEvent.Failed,
                    ToolName: DocumentSearchAgentTool.Name, ToolCallId: value.ToolCallId),
            _ => null
        };
    }

    private static bool TryGetSearchArguments(JToken raw, out JToken arguments)
    {
        arguments = default!;
        if (raw is not JObject obj || obj.Properties().Count() != 1 ||
            obj.Property("query")?.Value is not JValue { Type: JTokenType.String } query) return false;
        var value = query.Value<string>();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 500) return false;
        arguments = new JObject { ["query"] = value };
        return true;
    }

    private void Finish(Conversation conversation, long generation, Run run)
    {
        if (run.Finished) return;
        run.Finished = true;
        if (generation == conversation.Generation)
        {
            if (run.Cancellation.IsCancellationRequested)
            {
                conversation.State = "Cancelled";
                conversation.Error = null;
                conversation.ActiveProcessEvents.Clear();
            }
            conversation.Active = false;
            conversation.Turns++;
            conversation.Updated = clock.GetUtcNow();
            conversation.Version++;
            conversation.Run = null;
        }
        run.Cancellation.Dispose();
        run.Lease.Dispose();
    }

    private void Stop()
    {
        lock (sync)
        {
            stopping = true;
            foreach (var conversation in conversations.Values.Where(item => item.Active).ToArray())
            {
                var run = conversation.Run!;
                conversation.State = "Cancelling";
                conversation.Version++;
                run.Cancellation.Cancel();
                if (!run.Started) Finish(conversation, conversation.Generation, run);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        stoppingRegistration.Dispose();
    }

    private void Cleanup()
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromMinutes(30);
        foreach (var conversation in conversations.Values.Where(item => !item.Active && item.Updated < cutoff).ToArray())
            conversations.Remove(conversation.Id);
    }

    private static ConversationMessage CloneMessage(ConversationMessage message) => message with
    {
        Citations = message.Citations.ToArray(),
        ProcessEvents = message.ProcessEvents?.Select(process => process.DeepCopy()).ToArray()
    };

    private static int HistorySize(IReadOnlyList<TranscriptMessage> history) => history.Sum(message => message.Content.Sum(block => block switch
    {
        TextBlock text => text.Text.Length,
        ToolCallBlock call => call.Call.Arguments.ToString(Newtonsoft.Json.Formatting.None).Length,
        ToolResultBlock result => result.Result.Output?.ToString(Newtonsoft.Json.Formatting.None).Length ?? 0,
        _ => 0
    }));

    private static string Limit(string value, int maximum) => value.Length > maximum ? value[..maximum] : value;

    private sealed class Run(IDisposable lease)
    {
        public IDisposable Lease { get; } = lease;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool Started { get; set; }
        public bool Finished { get; set; }
    }

    private sealed class Conversation(string owner, string culture, string pathBase, DateTimeOffset created)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Owner { get; } = owner;
        public string Culture { get; } = culture;
        public string PathBase { get; } = pathBase;
        public List<ConversationMessage> Messages { get; } = [];
        public List<DocumentProcessEvent> ActiveProcessEvents { get; } = [];
        public List<AgentDiagnosticEvent> DiagnosticEvents { get; } = [];
        public IReadOnlyList<TranscriptMessage> History { get; set; } = [];
        public int NextLabel { get; set; }
        public long NextProcessSequence { get; set; }
        public int Turns { get; set; }
        public long Generation { get; set; }
        public long Version { get; set; }
        public bool Active { get; set; }
        public string State { get; set; } = "Completed";
        public string? Error { get; set; }
        public DateTimeOffset Updated { get; set; } = created;
        public Run? Run { get; set; }
    }
}
