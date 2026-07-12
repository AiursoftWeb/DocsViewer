using Aiursoft.CSTools.Tools;
using Aiursoft.Canon.TaskQueue;
using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.Canon.ScheduledTasks;
using Aiursoft.DbTools.Switchable;
using Aiursoft.Scanner;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.WebTools.Abstractions.Models;
using Aiursoft.DocsViewer.InMemory;
using Aiursoft.DocsViewer.MySql;
using Aiursoft.DocsViewer.Services.Authentication;
using Aiursoft.DocsViewer.Services.BackgroundJobs;
using Aiursoft.DocsViewer.Sqlite;
using Aiursoft.DocsViewer.Services;
using Aiursoft.UiStack.Layout;
using Aiursoft.UiStack.Navigation;
using Aiursoft.GptClient.Services;
using Aiursoft.Dotlang.Shared;
using Aiursoft.GitRunner;
using Microsoft.AspNetCore.Mvc.Razor;
using Aiursoft.ClickhouseLoggerProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.DocsViewer;

[ExcludeFromCodeCoverage]
public class Startup : IWebStartup
{
    public void ConfigureServices(IConfiguration configuration, IWebHostEnvironment environment, IServiceCollection services)
    {
        // AppSettings.
        services.Configure<AppSettings>(configuration.GetSection("AppSettings"));

        // Relational database
        var (connectionString, dbType, allowCache) = configuration.GetDbSettings();
        services.AddSwitchableRelationalDatabase(
            dbType: EntryExtends.IsInUnitTests() ? "InMemory" : dbType,
            connectionString: connectionString,
            supportedDbs:
            [
                new MySqlSupportedDb(allowCache: allowCache, splitQuery: false),
                new SqliteSupportedDb(allowCache: allowCache, splitQuery: true),
                new InMemorySupportedDb()
            ]);

        services.AddLogging(builder =>
        {
            builder.AddClickhouse(options => configuration.GetSection("Logging:Clickhouse").Bind(options));
        });

        // Authentication and Authorization
        services.AddTemplateAuth(configuration);

        // Services
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddAssemblyDependencies(typeof(Startup).Assembly);
        services.AddSingleton<NavigationState<Startup>>();
        services.AddHttpContextAccessor();
        services.AddScoped<DocumentLocalizationService>();
        services.AddScoped<ChatClient>();
        services.AddScoped<MarkdownShredder>();
        services.AddSingleton<DocumentEmbeddingCache>();
        services.AddSingleton<SearchRateLimiter>();
        services.AddScoped<DocumentVectorSearchService>();
        services.AddScoped<DocumentTreeService>();
        services.AddScoped<IDocumentTranslationService, DocumentTranslationService>();
        services.AddScoped<DocumentMarkdownRenderer>();
        services.AddGitRunner();

        // Background job infrastructure
        services.AddTaskQueueEngine();
        services.AddScheduledTaskEngine();

        // Background jobs
        var syncRepoJob = services.RegisterBackgroundJob<SyncDocsRepoJob>();
        var indexDocsJob = services.RegisterBackgroundJob<IndexDocumentsJob>();
        var localizeDocsJob = services.RegisterBackgroundJob<LocalizeDocumentsJob>();
        var localizeNavTitlesJob = services.RegisterBackgroundJob<LocalizeNavTitlesJob>();
        var generateEmbeddingsJob = services.RegisterBackgroundJob<GenerateEmbeddingsJob>();
        var refreshCacheJob = services.RegisterBackgroundJob<RefreshEmbeddingCacheJob>();
        var cleanupLocalizedDocsJob = services.RegisterBackgroundJob<CleanupLocalizedDocumentsJob>();
        var orphanAvatarCleanupJob = services.RegisterBackgroundJob<OrphanAvatarCleanupJob>();

        // Scheduled tasks (attach a schedule to any registered background job)
        services.RegisterScheduledTask(registration: syncRepoJob,             period: TimeSpan.FromHours(4), startDelay: TimeSpan.FromMinutes(1));
        services.RegisterScheduledTask(registration: indexDocsJob,            period: TimeSpan.FromHours(4), startDelay: TimeSpan.FromMinutes(20));
        services.RegisterScheduledTask(registration: localizeDocsJob,         period: TimeSpan.FromMinutes(30), startDelay: TimeSpan.FromMinutes(30));
        services.RegisterScheduledTask(registration: localizeNavTitlesJob,    period: TimeSpan.FromMinutes(30), startDelay: TimeSpan.FromMinutes(35));
        services.RegisterScheduledTask(registration: generateEmbeddingsJob,   period: TimeSpan.FromMinutes(30), startDelay: TimeSpan.FromMinutes(50));
        services.RegisterScheduledTask(registration: refreshCacheJob,         period: TimeSpan.FromHours(8), startDelay: TimeSpan.FromMinutes(1));
        services.RegisterScheduledTask(registration: cleanupLocalizedDocsJob, period: TimeSpan.FromHours(6), startDelay: TimeSpan.FromMinutes(55));

        services.RegisterScheduledTask(
            registration: orphanAvatarCleanupJob,
            period:     TimeSpan.FromHours(6),
            startDelay: TimeSpan.FromMinutes(5));

        // Controllers and localization
        services.AddControllersWithViews()
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
                options.SerializerSettings.ContractResolver = new DefaultContractResolver();
            })
            .AddApplicationPart(typeof(Startup).Assembly)
            .AddApplicationPart(typeof(UiStackLayoutViewModel).Assembly)
            .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
            .AddDataAnnotationsLocalization();
    }

    public void Configure(WebApplication app)
    {
        app.UseExceptionHandler("/Error/Code500");
        app.UseStatusCodePagesWithReExecute("/Error/Code{0}");
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDefaultControllerRoute();
        app.MapControllerRoute(
            name: "LegacyHtml",
            pattern: "{**path}",
            defaults: new { controller = "Documents", action = "Detail" });
    }
}
