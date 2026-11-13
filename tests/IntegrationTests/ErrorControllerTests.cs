namespace Aiursoft.DocsViewer.Tests.IntegrationTests;

[TestClass]
public class ErrorControllerTests : TestBase
{
    [TestMethod]
    [DataRow("/Error/Code500")]
    [DataRow("/Error/Code403?returnUrl=/dashboard")]
    [DataRow("/Error/Code400")]
    [DataRow("/Error/Code401")]
    [DataRow("/Error/Code404")]
    [DataRow("/Error/Code999")]
    public async Task GetError(string url)
    {
        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task MissingDocumentWithValidFormRemainsNotFound()
    {
        using var content = new StringContent("safe=1", null, "application/x-www-form-urlencoded");
        using var response = await Http.PostAsync("/error-page-regression/missing/route/extra", content);
        Assert.AreEqual(404, (int)response.StatusCode);
    }

    [TestMethod]
    public async Task MalformedDocumentRequestRendersInternalServerError()
    {
        // The catch-all document action binds the form before error rendering starts.
        using var content = new StringContent("safe%5B=1", null, "application/x-www-form-urlencoded");
        using var response = await Http.PostAsync("/error-page-regression/missing/route/extra", content);
        Assert.AreEqual(500, (int)response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "Internal Server Error");
    }

    [TestMethod]
    [DataRow(400)]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(500)]
    public async Task ErrorPageIgnoresMalformedFormAndUsesQueryReturnUrl(int code)
    {
        using var content = new StringContent("safe%5B=1&returnUrl=%2Fform-supplied&code=999", null,
            "application/x-www-form-urlencoded");
        using var response = await Http.PostAsync($"/Error/Code{code}?returnUrl=%2Fdashboard", content);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "href=\"/dashboard\"");
        Assert.IsFalse(html.Contains("href=\"/form-supplied\"", StringComparison.Ordinal));
    }
}
