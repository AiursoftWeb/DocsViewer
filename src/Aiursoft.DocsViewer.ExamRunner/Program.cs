using Aiursoft.DocsViewer.ExamRunner.Configuration;
using Aiursoft.DocsViewer.ExamRunner.Execution;

if (args is not ["--config", var configurationPath])
{
    Console.Error.WriteLine("Usage: dotnet run --project src/Aiursoft.DocsViewer.ExamRunner -- --config <path>");
    return 1;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler handler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += handler;
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
finally
{
    Console.CancelKeyPress -= handler;
}
