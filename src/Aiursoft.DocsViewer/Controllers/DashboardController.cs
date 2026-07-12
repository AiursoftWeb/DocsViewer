using Aiursoft.DocsViewer.Models.DashboardViewModels;
using Aiursoft.DocsViewer.Services;
using Aiursoft.DocsViewer.Services.Authentication;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Aiursoft.DocsViewer.Controllers;

/// <summary>
/// This controller handles the dashboard page that users are redirected to after login/register.
/// </summary>
[Authorize]
[LimitPerMin]
public class DashboardController(
    IStringLocalizer<DashboardController> localizer) : Controller
{
    [HttpGet]
    public IActionResult Index()
    {
        var displayName = User.Claims
            .FirstOrDefault(c => c.Type == UserClaimsPrincipalFactory.DisplayNameClaimType)?.Value
            ?? User.Identity?.Name
            ?? localizer["User"];

        return this.StackView(new IndexViewModel
        {
            DisplayName = displayName
        });
    }
}
