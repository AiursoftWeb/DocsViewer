using Aiursoft.Scanner.Abstractions;
using Aiursoft.DocsViewer.Configuration;
using Aiursoft.DocsViewer.Controllers;
using Aiursoft.DocsViewer.Entities;
using Aiursoft.DocsViewer.Services.Authentication;
using Aiursoft.DocsViewer.Services.FileStorage;
using Aiursoft.UiStack.Layout;
using Aiursoft.UiStack.Navigation;
using Aiursoft.UiStack.Views.Shared.Components.FooterMenu;
using Aiursoft.UiStack.Views.Shared.Components.LanguagesDropdown;
using Aiursoft.UiStack.Views.Shared.Components.MegaMenu;
using Aiursoft.UiStack.Views.Shared.Components.Navbar;
using Aiursoft.UiStack.Views.Shared.Components.SearchForm;
using Aiursoft.UiStack.Views.Shared.Components.SideAdvertisement;
using Aiursoft.UiStack.Views.Shared.Components.Sidebar;
using Aiursoft.UiStack.Views.Shared.Components.SideLogo;
using Aiursoft.UiStack.Views.Shared.Components.SideMenu;
using Aiursoft.UiStack.Views.Shared.Components.UserDropdown;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.DocsViewer.Services;

public class ViewModelArgsInjector(
    IStringLocalizer<ViewModelArgsInjector> localizer,
    StorageService storageService,
    NavigationState<Startup> navigationState,
    IAuthorizationService authorizationService,
    IOptions<AppSettings> appSettings,
    GlobalSettingsService globalSettingsService,
    DocumentTreeService documentTreeService,
    DocumentLocalizationService documentLocalization,
    SignInManager<User> signInManager) : IScopedDependency
{

    [ExcludeFromCodeCoverage]
    // ReSharper disable once UnusedMember.Local
    private void _useless_for_localizer()
    {
        // Titles, navbar strings.
        _ = localizer["Features"];
        _ = localizer["Index"];
        _ = localizer["Directory"];
        _ = localizer["Users"];
        _ = localizer["Roles"];
        _ = localizer["Administration"];
        _ = localizer["System"];
        _ = localizer["Info"];
        _ = localizer["Manage"];
        _ = localizer["Login"];
        _ = localizer["System Info"];
        _ = localizer["Create User"];
        _ = localizer["User Details"];
        _ = localizer["Edit User"];
        _ = localizer["Delete User"];
        _ = localizer["Create Role"];
        _ = localizer["Role Details"];
        _ = localizer["Edit Role"];
        _ = localizer["Delete Role"];
        _ = localizer["Change Profile"];
        _ = localizer["Change Avatar"];
        _ = localizer["Change Password"];
        _ = localizer["Home"];
        _ = localizer["Settings"];
        _ = localizer["Profile Settings"];
        _ = localizer["Personal"];
        _ = localizer["Unauthorized"];
        _ = localizer["Error"];
        _ = localizer["Permissions"];
        _ = localizer["Background Jobs"];
        _ = localizer["Global Settings"];

        _ = localizer["Access Denied"];
        _ = localizer["Bad Request"];
        _ = localizer["Dashboard"];
        _ = localizer["Internal Server Error"];
        _ = localizer["Lockout"];
        _ = localizer["Not Found"];
        _ = localizer["Permission Details"];
        _ = localizer["Register"];
        _ = localizer["Search documents…"];
    }

    public void InjectSimple(
        HttpContext context,
        UiStackLayoutViewModel toInject)
    {
        toInject.PageTitle = localizer[toInject.PageTitle ?? "View"];
        toInject.AppName = globalSettingsService.GetSettingValueAsync(SettingsMap.ProjectName).GetAwaiter().GetResult();
        toInject.Theme = UiTheme.Light;
        toInject.SidebarTheme = UiSidebarTheme.Default;
        toInject.Layout = UiLayout.Fluid;
        toInject.ContentNoPadding = true;
    }

    public void Inject(
        HttpContext context,
        UiStackLayoutViewModel toInject)
    {
        var preferDarkTheme = context.Request.Cookies[ThemeController.ThemeCookieKey] == true.ToString();
        var projectName = globalSettingsService.GetSettingValueAsync(SettingsMap.ProjectName).GetAwaiter().GetResult();
        var brandName = globalSettingsService.GetSettingValueAsync(SettingsMap.BrandName).GetAwaiter().GetResult();
        var brandHomeUrl = globalSettingsService.GetSettingValueAsync(SettingsMap.BrandHomeUrl).GetAwaiter().GetResult();
        toInject.PageTitle = localizer[toInject.PageTitle ?? "View"];
        toInject.AppName = projectName;
        toInject.Theme = preferDarkTheme ? UiTheme.Dark : UiTheme.Light;
        toInject.SidebarTheme = preferDarkTheme ? UiSidebarTheme.Dark : UiSidebarTheme.Default;
        toInject.Layout = UiLayout.Fluid;
        toInject.FooterMenu = new FooterMenuViewModel
        {
            AppBrand = new Link { Text = brandName, Href = brandHomeUrl },
            Links =
            [
                new Link { Text = localizer["Home"], Href = "/" },
                new Link { Text = "Aiursoft", Href = "https://www.aiursoft.com" },
            ]
        };
        toInject.Navbar = new NavbarViewModel
        {
            ThemeSwitchApiCallEndpoint = "/api/switch-theme",
            SearchForm = new SearchFormViewModel
            {
                SearchUrl = "/Documents/Search",
                SearchParam = "q",
                Placeholder = localizer["Search documents…"]
            }
        };

        var currentViewingController = context.GetRouteValue("controller")?.ToString();
        var currentPath = context.GetRouteValue("path")?.ToString();
        var currentDocId = context.GetRouteValue("id")?.ToString();
        var navGroupsForView = new List<NavGroup>();

        foreach (var groupDef in navigationState.NavMap)
        {
            var itemsForView = new List<CascadedSideBarItem>();
            foreach (var itemDef in groupDef.Items)
            {
                var linksForView = new List<CascadedLink>();
                foreach (var linkDef in itemDef.Links)
                {
                    bool isVisible;
                    if (string.IsNullOrEmpty(linkDef.RequiredPolicy))
                    {
                        isVisible = true;
                    }
                    else
                    {
                        var authResult = authorizationService.AuthorizeAsync(context.User, linkDef.RequiredPolicy).GetAwaiter().GetResult();
                        isVisible = authResult.Succeeded;
                    }

                    if (isVisible)
                    {
                        linksForView.Add(new CascadedLink
                        {
                            Href = linkDef.Href,
                            Text = localizer[linkDef.Text]
                        });
                    }
                }

                if (linksForView.Any())
                {
                    itemsForView.Add(new CascadedSideBarItem
                    {
                        UniqueId = itemDef.UniqueId,
                        Text = localizer[itemDef.Text],
                        LucideIcon = itemDef.Icon,
                        IsActive = linksForView.Any(l =>
                        {
                            var hrefController = l.Href.TrimStart('/').Split('/').FirstOrDefault();
                            return string.Equals(hrefController, currentViewingController, StringComparison.OrdinalIgnoreCase);
                        }),
                        Links = linksForView
                    });
                }
            }

            if (itemsForView.Any())
            {
                navGroupsForView.Add(new NavGroup
                {
                    Name = localizer[groupDef.Name],
                    Items = itemsForView.Select(t => (SideBarItem)t).ToList()
                });
            }
        }

        var documentTree = documentTreeService.GetTreeAsync().GetAwaiter().GetResult();
        var allDocsForLocalization = CollectDocumentsFromTree(documentTree);
        var (localizedTitles, _) = documentLocalization.LoadLocalizedStringsAsync(allDocsForLocalization).GetAwaiter().GetResult();
        var docNavGroups = new List<NavGroup>();

        foreach (var l1Node in documentTree)
        {
            if (l1Node.Document != null) continue; // Handled in root group below
            
            var navGroup = new NavGroup
            {
                Name = l1Node.Name,
                Items = new List<SideBarItem>()
            };

            foreach (var l2Node in l1Node.Children)
            {
                navGroup.Items.Add(BuildSideBarItem(l2Node, currentViewingController, currentDocId, currentPath, localizedTitles));
            }
            
            if (navGroup.Items.Any())
            {
                docNavGroups.Add(navGroup);
            }
        }
        
        var rootFiles = documentTree.Where(n => n.Document != null).ToList();
        if (rootFiles.Any())
        {
            var rootGroup = new NavGroup
            {
                Name = localizer["Documentation"],
                Items = rootFiles.Select(n => 
                {
                    var htmlPath = n.Document!.FilePath[..^3].Replace('\\', '/') + ".html";
                    return (SideBarItem)new LinkSideBarItem
                    {
                        LucideIcon = "file-text",
                        Text = localizedTitles.GetValueOrDefault(n.Document!.Id, n.Document!.Title),
                        Href = $"/{htmlPath}",
                        IsActive = (currentViewingController == "Documents" && currentDocId == n.Document.Id.ToString()) ||
                                   string.Equals(currentPath, htmlPath, StringComparison.OrdinalIgnoreCase)
                    };
                }).ToList()
            };
            docNavGroups.Insert(0, rootGroup);
        }

        // Insert docs first, then Settings (Personal), then Administration
        navGroupsForView.InsertRange(0, docNavGroups);

        toInject.Sidebar = new SidebarViewModel
        {
            SideLogo = new SideLogoViewModel
            {
                AppName = projectName,
                LogoUrl = GetLogoUrl(context).GetAwaiter().GetResult(),
                Href = "/"
            },
            SideMenu = new SideMenuViewModel
            {
                Groups = navGroupsForView
            }
        };

        var currentCulture = context.Features
            .Get<IRequestCultureFeature>()?
            .RequestCulture.Culture.Name; // zh-CN

        // ReSharper disable once RedundantNameQualifier
        var suppportedCultures = Aiursoft.WebTools.OfficialPlugins.LocalizationPlugin.SupportedCultures
            .Select(c => new LanguageSelection
            {
                Link = $"/Culture/Set?culture={c.Key}&returnUrl={context.Request.Path}",
                Name = c.Value // 中文 - 中国
            })
            .ToArray();

        // ReSharper disable once RedundantNameQualifier
        toInject.Navbar.LanguagesDropdown = new LanguagesDropdownViewModel
        {
            Languages = suppportedCultures,
            SelectedLanguage = new LanguageSelection
            {
                Name = Aiursoft.WebTools.OfficialPlugins.LocalizationPlugin.SupportedCultures[currentCulture ?? "en-US"],
                Link = "#",
            }
        };

        if (signInManager.IsSignedIn(context.User))
        {
            var avatarPath = context.User.Claims.First(c => c.Type == UserClaimsPrincipalFactory.AvatarClaimType)
                .Value;
            toInject.Navbar.UserDropdown = new UserDropdownViewModel
            {
                UserName = context.User.Claims.First(c => c.Type == UserClaimsPrincipalFactory.DisplayNameClaimType).Value,
                UserAvatarUrl = $"{storageService.RelativePathToInternetUrl(avatarPath)}?w=100&square=true",
                IconLinkGroups =
                [
                    new IconLinkGroup
                    {
                        Links =
                        [
                            new IconLink { Icon = "user", Text = localizer["Profile"], Href = "/Manage" },
                        ]
                    },
                    new IconLinkGroup
                    {
                        Links =
                        [
                            new IconLink { Icon = "log-out", Text = localizer["Sign out"], Href = "/Account/Logoff" }
                        ]
                    }
                ]
            };
        }
        else
        {
            toInject.Sidebar.SideAdvertisement = new SideAdvertisementViewModel
            {
                Title = localizer["Login"],
                Description = localizer["Login to get access to all features."],
                Href = "/Account/Login",
                ButtonText = localizer["Login"]
            };

            var allowRegister = appSettings.Value.Local.AllowRegister;
            var links = new List<IconLink>
            {
                new()
                {
                    Text = localizer["Login"],
                    Href = "/Account/Login",
                    Icon = "user"
                }
            };
            if (allowRegister && appSettings.Value.LocalEnabled)
            {
                links.Add(new IconLink
                {
                    Text = localizer["Register"],
                    Href = "/Account/Register",
                    Icon = "user-plus"
                });
            }
            toInject.Navbar.UserDropdown = new UserDropdownViewModel
            {
                UserName = localizer["Click to login"],
                UserAvatarUrl = string.Empty,
                IconLinkGroups =
                [
                    new IconLinkGroup
                    {
                        Links = links.ToArray()
                    }
                ]
            };
        }
    }


    private async Task<string> GetLogoUrl(HttpContext context)
    {
        var logoPath = await globalSettingsService.GetSettingValueAsync(SettingsMap.ProjectLogo);
        if (string.IsNullOrWhiteSpace(logoPath))
        {
            return "/logo.svg";
        }
        return storageService.RelativePathToInternetUrl(logoPath, context);
    }

    private SideBarItem BuildSideBarItem(DocumentTreeNode node, string? currentController, string? currentDocId, string? currentPath, Dictionary<int, string> localizedTitles)
    {
        if (node.Document != null)
        {
            var htmlPath = node.Document.FilePath[..^3].Replace('\\', '/') + ".html";
            return new LinkSideBarItem
            {
                LucideIcon = "file-text",
                Text = localizedTitles.GetValueOrDefault(node.Document.Id, node.Document.Title),
                Href = $"/{htmlPath}",
                IsActive = (currentController == "Documents" && currentDocId == node.Document.Id.ToString()) ||
                           string.Equals(currentPath, htmlPath, StringComparison.OrdinalIgnoreCase)
            };
        }
        else
        {
            var deepItem = new NestedSideBarItem
            {
                UniqueId = "node_" + Math.Abs(node.Path.GetHashCode()),
                LucideIcon = "folder",
                Text = node.Name,
                IsActive = false,
                Children = new List<SideBarItem>()
            };

            foreach (var child in node.Children)
            {
                var builtChild = BuildSideBarItem(child, currentController, currentDocId, currentPath, localizedTitles);
                if (builtChild.IsActive)
                {
                    deepItem.IsActive = true;
                }
                deepItem.Children.Add(builtChild);
            }
            
            return deepItem;
        }
    }

    /// <summary>
    /// Recursively collects all Document references from a tree of DocumentTreeNode,
    /// so we can batch-load localized titles for the sidebar in one query.
    /// </summary>
    private static List<Document> CollectDocumentsFromTree(List<DocumentTreeNode> nodes)
    {
        var result = new List<Document>();
        foreach (var node in nodes)
        {
            if (node.Document != null)
                result.Add(node.Document);
            if (node.Children.Count > 0)
                result.AddRange(CollectDocumentsFromTree(node.Children));
        }
        return result;
    }
}
