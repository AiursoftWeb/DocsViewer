using Aiursoft.GptClient.Services;
using Microsoft.Extensions.Options;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public class LocalizationChatClientTests
{
    [TestMethod]
    public void LocalizationTimeoutAllowsGatewayQueuing()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DbType"] = "InMemory",
            ["ConnectionStrings:DefaultConnection"] = "DataSource=:memory:",
            ["AppSettings:AuthProvider"] = "Local"
        });
        new Startup().ConfigureServices(builder.Configuration, builder.Environment, builder.Services);
        HttpClient? localizationHttpClient = null;
        builder.Services.AddHttpClient(Options.DefaultName, client => localizationHttpClient = client);

        using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ChatClient>();

        Assert.IsNotNull(localizationHttpClient);
        Assert.AreEqual(TimeSpan.FromSeconds(1000), localizationHttpClient.Timeout);

        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        using var defaultClient = factory.CreateClient();
        using var agentClient = factory.CreateClient("DocsViewerAgentModel");
        Assert.AreEqual(TimeSpan.FromSeconds(100), defaultClient.Timeout);
        Assert.AreEqual(TimeSpan.FromSeconds(60), agentClient.Timeout);
    }
}
