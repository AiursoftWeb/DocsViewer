using Aiursoft.DocsViewer.Configuration;

namespace Aiursoft.DocsViewer.Tests.IntegrationTests;

[TestClass]
public class GlobalSettingsControllerTests : TestBase
{
    [TestMethod]
    public async Task TestGlobalSettingsWorkflow()
    {
        await LoginAsAdmin();

        // 1. Index
        var indexResponse = await Http.GetAsync("/GlobalSettings/Index");
        indexResponse.EnsureSuccessStatusCode();
        var indexHtml = await indexResponse.Content.ReadAsStringAsync();
        Assert.Contains("Global Settings", indexHtml);
        Assert.Contains(SettingsMap.ProjectName, indexHtml);
        // 2. Edit (POST)
        var newProjectName = "My New Project " + Guid.NewGuid();
        var editResponse = await PostForm("/GlobalSettings/Edit", new Dictionary<string, string>
        {
            { "Key", SettingsMap.ProjectName },
            { "Value", newProjectName }
        }, tokenUrl: "/GlobalSettings/Index");
        AssertRedirect(editResponse, "/GlobalSettings");

        // 3. Verify Edit
        var indexResponse2 = await Http.GetAsync("/GlobalSettings/Index");
        var indexHtml2 = await indexResponse2.Content.ReadAsStringAsync();
        Assert.Contains(newProjectName, indexHtml2);

        var customInstruction = "Use concise language.\nDo not answer without citations.";
        var customEditResponse = await PostForm("/GlobalSettings/Edit", new Dictionary<string, string>
        {
            { "Key", SettingsMap.OpenAiAgentCustomInstruction },
            { "Value", customInstruction }
        }, tokenUrl: "/GlobalSettings/Index");
        AssertRedirect(customEditResponse, "/GlobalSettings");
        var customSettingsHtml = await (await Http.GetAsync("/GlobalSettings/Index")).Content.ReadAsStringAsync();
        Assert.Contains("Use concise language.", customSettingsHtml);
        Assert.Contains("Do not answer without citations.", customSettingsHtml);

        // 4. Edit (invalid key)
        var invalidEditResponse = await PostForm("/GlobalSettings/Edit", new Dictionary<string, string>
        {
            { "Key", "InvalidKey" },
            { "Value", "SomeValue" }
        }, tokenUrl: "/GlobalSettings/Index");
        AssertRedirect(invalidEditResponse, "/GlobalSettings");
    }

    [TestMethod]
    public async Task TestEditInvalidModel()
    {
        await LoginAsAdmin();
        // Missing Key
        var response = await PostForm("/GlobalSettings/Edit", new Dictionary<string, string>
        {
            { "Value", "SomeValue" }
        }, tokenUrl: "/GlobalSettings/Index");
        AssertRedirect(response, "/GlobalSettings");
    }
}
