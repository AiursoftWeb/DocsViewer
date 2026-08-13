namespace Aiursoft.DocsViewer.Tests.IntegrationTests;

[TestClass]
public class HomeControllerTests : TestBase
{
    [TestMethod]
    public async Task GetIndex()
    {
        var url = "/";
        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "document-page-container");
        StringAssert.Contains(html, "/scripts/document-outline.js");
    }
}
