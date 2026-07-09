using Aiursoft.DbTools;
using Aiursoft.DbTools.InMemory;
using Aiursoft.DocsViewer.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aiursoft.DocsViewer.InMemory;

public class InMemorySupportedDb : SupportedDatabaseType<DocsViewerDbContext>
{
    public override string DbType => "InMemory";

    public override IServiceCollection RegisterFunction(IServiceCollection services, string connectionString)
    {
        return services.AddAiurInMemoryDb<InMemoryContext>();
    }

    public override DocsViewerDbContext ContextResolver(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredService<InMemoryContext>();
    }
}
