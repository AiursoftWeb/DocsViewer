using Aiursoft.DocsViewer.ExamRunner.Configuration;
using Aiursoft.DocsViewer.ExamRunner.Execution;

if (args is not ["--config", var configurationPath])
{
    Console.Error.WriteLine("Usage: dotnet run --project src/Aiursoft.DocsViewer.ExamRunner -- --config <path>");
    return 1;
}

using var cancellation = new ConsoleCancellation();
try
{
    var configuration = await ExamConfigurationLoader.LoadAsync(configurationPath, cancellation.Token);
    var result = await new ExamOrchestrator().RunAsync(configuration, cancellation.Token);
    Console.WriteLine($"Document exam reports: {result.OutputDirectory}");
    return result.ExitCode;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Document exam was cancelled.");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Document exam failed: {exception.GetType().Name}");
    return 1;
}

internal sealed class ConsoleCancellation : IDisposable
{
    private readonly CancellationTokenSource _source = new();

    public ConsoleCancellation() => Console.CancelKeyPress += OnCancelKeyPress;

    public CancellationToken Token => _source.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        _source.Cancel();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        _source.Dispose();
    }
}
