using Aiursoft.Canon.TaskQueue;

namespace Aiursoft.DocsViewer.Services.Agents;

/// <summary>Application adapter for the existing fixed-lane task queue.</summary>
public interface IDocumentConversationQueue
{
    void Enqueue(int lane, string name, Func<Task> work);
}

public sealed class DocumentConversationQueue(ServiceTaskQueue queue) : IDocumentConversationQueue
{
    public void Enqueue(int lane, string name, Func<Task> work) =>
        queue.QueueWithDependency<IServiceProvider>($"DocsViewerAgent-{lane}", name, _ => work());
}
