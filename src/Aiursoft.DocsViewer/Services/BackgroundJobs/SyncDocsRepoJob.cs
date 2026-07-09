using Aiursoft.CSTools.Tools;
using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.GitRunner;
using Aiursoft.GitRunner.Models;

namespace Aiursoft.DocsViewer.Services.BackgroundJobs;

public class SyncDocsRepoJob(
    GlobalSettingsService settingsService,
    WorkspaceManager workspaceManager,
    ILogger<SyncDocsRepoJob> logger,
    IWebHostEnvironment env) : IBackgroundJob
{
    public string Name => "Sync Docs Repo Job";

    public string Description => "Syncs the docs repository periodically.";

    public async Task ExecuteAsync()
    {
        var url = await settingsService.GetSettingValueAsync("DocsRepoUrl");
        var backupUrl = await settingsService.GetSettingValueAsync("DocsRepoBackupUrl");

        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var path = Path.Combine(env.ContentRootPath, "App_Data", "DocsRepo");

        Directory.CreateDirectory(path);

        try
        {
            logger.LogInformation("SyncDocsRepoJob: cloning from primary URL '{RepoUrl}'.", url);
            await workspaceManager.ResetRepo(path, branch: null, endPoint: url, cloneMode: CloneMode.Full);
            logger.LogInformation("SyncDocsRepoJob: repo is up to date.");
        }
        catch (TimeoutException)
        {
            if (string.IsNullOrWhiteSpace(backupUrl) || backupUrl == url)
                throw;

            logger.LogWarning("SyncDocsRepoJob: primary URL '{RepoUrl}' timed out. Cleaning up and falling back to backup URL '{BackupUrl}'.", url, backupUrl);
            FolderDeleter.DeleteByForce(path, keepFolder: true);
            Directory.CreateDirectory(path);
            await workspaceManager.ResetRepo(path, branch: null, endPoint: backupUrl, cloneMode: CloneMode.Full);
            logger.LogInformation("SyncDocsRepoJob: repo is up to date (via backup URL).");
        }
        catch (Exception)
        {
            logger.LogError("SyncDocsRepoJob: failed to sync repo. Cleaning up local directory to avoid partial state.");
            FolderDeleter.DeleteByForce(path, keepFolder: true);
            throw;
        }
    }
}
