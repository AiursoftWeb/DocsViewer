using Aiursoft.AgentKit;
using Aiursoft.AgentKit.Messages;
using Aiursoft.DocsViewer.Services.Agents;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public sealed class DocumentConversationTests
{
    [TestMethod]
    public async Task QueuedCancelReleasesLeaseAndStaleCallbackCannotExecute()
    {
        using var test = new Fixture();
        var id = (await test.Send()).ConversationId!.Value;
        Assert.IsTrue(test.Service.Cancel("owner", id));
        Assert.IsTrue(test.Service.Cancel("owner", id));
        Assert.AreEqual("Cancelled", test.Service.Status("owner", id)!.State);
        using var lease = (await test.Limiter.AcquireAsync("owner")).Lease;
        Assert.IsNotNull(lease);
        await test.Queue.RunNext();
        Assert.AreEqual(0, test.Executor.Calls);
    }

    [TestMethod]
    public async Task ShutdownFinalizesPendingWorkWithoutQueueCallback()
    {
        using var test = new Fixture();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++) ids.Add((await test.Send($"owner{i}")).ConversationId!.Value);
        test.Lifetime.StopApplication();
        test.Service.Dispose();
        for (var i = 0; i < 4; i++)
        {
            Assert.AreEqual("Cancelled", test.Service.Status($"owner{i}", ids[i])!.State);
            using var lease = (await test.Limiter.AcquireAsync($"owner{i}")).Lease;
            Assert.IsNotNull(lease);
        }
        Assert.AreEqual("Stopping", (await test.Send("other")).Error);
        while (test.Queue.Work.Count > 0) await test.Queue.RunNext();
        Assert.AreEqual(0, test.Executor.Calls);
    }

    [TestMethod]
    public async Task RunningCancellationRetainsLeaseUntilIgnoringWorkerExits()
    {
        using var test = new Fixture();
        test.Executor.Block = true;
        var id = (await test.Send()).ConversationId!.Value;
        var work = test.Queue.RunNext();
        await test.Executor.Started.Task;
        test.Service.Cancel("owner", id);
        test.Lifetime.StopApplication();
        Assert.AreEqual("Cancelling", test.Service.Status("owner", id)!.State);
        Assert.AreEqual(AgentLimitStatus.ConcurrencyLimited, (await test.Limiter.AcquireAsync("owner")).Status);
        test.Executor.Release.SetResult();
        await work;
        var snapshot = test.Service.Status("owner", id)!;
        Assert.AreEqual("Cancelled", snapshot.State);
        Assert.AreEqual(1, snapshot.Messages.Count);
        using var lease = (await test.Limiter.AcquireAsync("owner")).Lease;
        Assert.IsNotNull(lease);
    }

    [TestMethod]
    public async Task EnqueueFailureRollsBackAndReleasesLease()
    {
        using var test = new Fixture();
        test.Queue.Fail = true;
        Assert.AreEqual("QueueFailure", (await test.Send()).Error);
        using var lease = (await test.Limiter.AcquireAsync("owner")).Lease;
        Assert.IsNotNull(lease);
        Assert.AreEqual(0, test.Executor.Calls);
    }

    [TestMethod]
    public async Task SimultaneousContinuationAdmitsOnlyOneTurn()
    {
        using var test = new Fixture();
        var id = (await test.Send()).ConversationId!.Value;
        await test.Queue.RunNext();
        var admissions = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => test.Send("owner", id))));
        Assert.AreEqual(1, admissions.Count(a => a.ConversationId.HasValue));
        Assert.AreEqual(1, test.Queue.Work.Count);
    }

    [TestMethod]
    public async Task CompletedConversationExposesOnlyCuratedSearchEvents()
    {
        using var test = new Fixture();
        test.Executor.Events =
        [
            new(1, 1, DocumentProcessEvent.ToolExecution, DocumentProcessEvent.Succeeded,
                ToolName: DocumentSearchAgentTool.Name,
                ResultCount: 1,
                Citations: [new ConversationCitation("[D1]", "Safe source", "/Documents/Detail?path=safe.md")]),
            new("UnknownTool", "Raw error with <script>secret</script>")
        ];
        var id = (await test.Send()).ConversationId!.Value;
        await test.Queue.RunNext();

        var message = test.Service.Status("owner", id)!.Messages.Single(x => x.Role == "assistant");
        Assert.AreEqual(1, message.ProcessEvents!.Count);
        Assert.AreEqual(DocumentProcessEvent.ToolExecution, message.ProcessEvents[0].Kind);
        Assert.AreEqual(DocumentProcessEvent.Succeeded, message.ProcessEvents[0].Status);
        Assert.IsFalse(message.ProcessEvents.Any(x => x.Status.Contains("secret", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ProcessEventsRemainOwnerScopedAndCancelledTurnsExposeNone()
    {
        using var test = new Fixture();
        test.Executor.Block = true;
        test.Executor.Events = [new(DocumentProcessEvent.DocumentationSearch, DocumentProcessEvent.Succeeded)];
        var id = (await test.Send()).ConversationId!.Value;
        var work = test.Queue.RunNext();
        await test.Executor.Started.Task;
        test.Service.Cancel("owner", id);
        test.Executor.Release.SetResult();
        await work;

        Assert.IsNull(test.Service.Status("other", id));
        var owner = test.Service.Status("owner", id)!;
        Assert.AreEqual("Cancelled", owner.State);
        Assert.IsFalse(owner.Messages.Any(message => message.ProcessEvents is { Count: > 0 }));
    }

    [TestMethod]
    public async Task IdleExpiryAndPerUserCapacityDoNotEvictActiveWork()
    {
        using var test = new Fixture();
        Guid id = default;
        for (var i = 0; i < 5; i++)
        {
            id = (await test.Send()).ConversationId!.Value;
            await test.Queue.RunNext();
            test.Clock.Now += TimeSpan.FromMinutes(2);
        }
        Assert.AreEqual("Capacity", (await test.Send()).Error);
        test.Clock.Now += TimeSpan.FromMinutes(31);
        Assert.IsNull(test.Service.Status("owner", id));
        id = (await test.Send()).ConversationId!.Value;
        test.Clock.Now += TimeSpan.FromMinutes(31);
        Assert.IsNotNull(test.Service.Status("owner", id));
        test.Service.Cancel("owner", id);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }

    private sealed class Queue : IDocumentConversationQueue
    {
        public readonly Queue<Func<Task>> Work = new();
        public bool Fail;
        public void Enqueue(int lane, string name, Func<Task> work)
        {
            Assert.IsTrue(lane is >= 0 and < 4);
            if (Fail) throw new InvalidOperationException("test enqueue failure");
            Work.Enqueue(work);
        }
        public Task RunNext() => Work.Dequeue()();
    }

    private sealed class Executor : IDocumentTurnExecutor
    {
        public int Calls;
        public bool Block;
        public IReadOnlyList<DocumentProcessEvent> Events { get; set; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DocumentTurnResult> ExecuteTurnAsync(
            string question,
            IReadOnlyList<TranscriptMessage> history,
            int nextLabel,
            string culture,
            string? pathBase,
            Func<AgentRunEvent, CancellationToken, ValueTask>? onProgress,
            Func<DocumentProcessEvent, CancellationToken, ValueTask>? onProcess,
            CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            if (Block) await Release.Task; // Deliberately ignore cancellation to test lease ownership.
            return new(new("safe answer", [], true), [TranscriptMessage.User(question),
                TranscriptMessage.Assistant([new TextBlock("safe answer")])], nextLabel, Events);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Clock Clock { get; } = new();
        public Lifetime Lifetime { get; } = new();
        public Queue Queue { get; } = new();
        public Executor Executor { get; } = new();
        public AgentRequestLimiter Limiter { get; }
        public DocumentConversationService Service { get; }
        private readonly ServiceProvider provider;
        public Fixture()
        {
            provider = new ServiceCollection().AddScoped<IDocumentTurnExecutor>(_ => Executor).BuildServiceProvider();
            Limiter = new(Clock);
            Service = new(Queue, provider.GetRequiredService<IServiceScopeFactory>(), Limiter, Lifetime, Clock);
        }
        public Task<ConversationAdmission> Send(string owner = "owner", Guid? id = null) =>
            Service.SendAsync(owner, "question", id, "en-US", "");
        public void Dispose() { Service.Dispose(); provider.Dispose(); Lifetime.Dispose(); }
    }
}
