using Aiursoft.DocsViewer.Models;

namespace Aiursoft.DocsViewer.Configuration;

public class SettingsMap
{
    public const string ProjectName = "ProjectName";
    public const string BrandName = "BrandName";
    public const string BrandHomeUrl = "BrandHomeUrl";
    public const string ProjectLogo = "ProjectLogo";
    public const string AllowUserAdjustNickname = "Allow_User_Adjust_Nickname";
    public const string Icp = "Icp";
    
    public const string DocsRepoUrl = "DocsRepoUrl";
    public const string DocsRepoBackupUrl = "DocsRepoBackupUrl";
    public const string DocsRootPath = "DocsRootPath";
    public const string DocsHomePage = "DocsHomePage";
    
    public const string OpenAiInstance = "OpenAiInstance";
    public const string OpenAiLocalizationModel = "OpenAiLocalizationModel";
    public const string OpenAiApiToken = "OpenAiApiToken";
    
    public const string EmbeddingOllamaInstance = "EmbeddingOllamaInstance";
    public const string EmbeddingModel = "EmbeddingModel";
    public const string EmbeddingApiToken = "EmbeddingApiToken";
    
    public const string EnableEmbeddingBasedSearch = "EnableEmbeddingBasedSearch";
    public const string LocalizationLanguages = "LocalizationLanguages";
    public const string EmbeddingQueryCacheLimit = "EmbeddingQueryCacheLimit";
    public const string MaxCommentsPerDayPerUser = "MaxCommentsPerDayPerUser";

    public class FakeLocalizer
    {
        public string this[string name] => name;
    }

    private static readonly FakeLocalizer Localizer = new();

    public static readonly List<GlobalSettingDefinition> Definitions = new()
    {
        new GlobalSettingDefinition
        {
            Key = ProjectName,
            Name = Localizer["Project Name"],
            Description = Localizer["The name of the project displayed in the frontend."],
            Type = SettingType.Text,
            DefaultValue = "Aiursoft DocsViewer"
        },
        new GlobalSettingDefinition
        {
            Key = BrandName,
            Name = Localizer["Brand Name"],
            Description = Localizer["The brand name displayed in the footer."],
            Type = SettingType.Text,
            DefaultValue = "Aiursoft"
        },
        new GlobalSettingDefinition
        {
            Key = BrandHomeUrl,
            Name = Localizer["Brand Home URL"],
            Description = Localizer[" The link to the brand's home page."],
            Type = SettingType.Text,
            DefaultValue = "https://www.aiursoft.com/"
        },
        new GlobalSettingDefinition
        {
            Key = ProjectLogo,
            Name = Localizer["Project Logo"],
            Description = Localizer["The logo of the project displayed in the navbar and footer. Support jpg, png, svg."],
            Type = SettingType.File,
            DefaultValue = "",
            Subfolder = "project-logo",
            AllowedExtensions = "jpg png svg",
            MaxSizeInMb = 5
        },
        new GlobalSettingDefinition
        {
            Key = AllowUserAdjustNickname,
            Name = Localizer["Allow User Adjust Nickname"],
            Description = Localizer["Allow users to adjust their nickname in the profile management page."],
            Type = SettingType.Bool,
            DefaultValue = "True"
        },
        new GlobalSettingDefinition
        {
            Key = Icp,
            Name = Localizer["ICP Number"],
            Description = Localizer["The ICP license number for China mainland users. Leave empty to hide."],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = DocsRepoUrl,
            Name = Localizer["Docs Repository URL"],
            Description = Localizer["The URL of the git repository containing the markdown documents."],
            Type = SettingType.Text,
            DefaultValue = "https://gitlab.aiursoft.com/aiursoft/anduinos-docs.git"
        },
        new GlobalSettingDefinition
        {
            Key = DocsRepoBackupUrl,
            Name = Localizer["Docs Repository Backup URL"],
            Description = Localizer["The backup URL of the git repository. Used if the primary URL fails."],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = DocsRootPath,
            Name = Localizer["Docs Root Path"],
            Description = Localizer["The relative path within the repo where the documentation is located (e.g., '/', '/docs')."],
            Type = SettingType.Text,
            DefaultValue = "/Docs"
        },
        new GlobalSettingDefinition
        {
            Key = DocsHomePage,
            Name = Localizer["Docs Home Page"],
            Description = Localizer["The home page path relative to the repository root. This is not affected by Docs Root Path (e.g., '/README.md')."],
            Type = SettingType.Text,
            DefaultValue = "/README.md"
        },
        new GlobalSettingDefinition
        {
            Key = OpenAiInstance,
            Name = Localizer["OpenAI Instance"],
            Description = Localizer["The base URL of the OpenAI-compatible API for document localization."],
            Type = SettingType.Text,
            DefaultValue = "https://api.openai.com"
        },
        new GlobalSettingDefinition
        {
            Key = OpenAiLocalizationModel,
            Name = Localizer["OpenAI Localization Model"],
            Description = Localizer["The model name used for document translation."],
            Type = SettingType.Text,
            DefaultValue = "gpt-3.5-turbo"
        },
        new GlobalSettingDefinition
        {
            Key = OpenAiApiToken,
            Name = Localizer["OpenAI API Token"],
            Description = Localizer["The API token for the translation service."],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingOllamaInstance,
            Name = Localizer["Embedding API Instance"],
            Description = Localizer["The base URL of the OpenAI-compatible API for vector embeddings."],
            Type = SettingType.Text,
            DefaultValue = "http://localhost:11434"
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingModel,
            Name = Localizer["Embedding Model"],
            Description = Localizer["The embedding model to use (e.g. bge-m3)."],
            Type = SettingType.Text,
            DefaultValue = "bge-m3"
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingApiToken,
            Name = Localizer["Embedding API Token"],
            Description = Localizer["The API token for the embedding service. Can be empty for local Ollama."],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = EnableEmbeddingBasedSearch,
            Name = Localizer["Enable Vector Search"],
            Description = Localizer["Enable semantic vector search using embeddings. Turn off to fallback to keyword search only."],
            Type = SettingType.Bool,
            DefaultValue = "True"
        },
        new GlobalSettingDefinition
        {
            Key = LocalizationLanguages,
            Name = Localizer["Localization Languages"],
            Description = Localizer["Comma-separated list of target cultures for translation. e.g. 'en-US,zh-CN'"],
            Type = SettingType.Text,
            DefaultValue = "en-US"
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingQueryCacheLimit,
            Name = Localizer["Embedding Query Cache Limit"],
            Description = Localizer["Maximum number of search queries to cache their embeddings in database."],
            Type = SettingType.Number,
            DefaultValue = "2000"
        },
        new GlobalSettingDefinition
        {
            Key = MaxCommentsPerDayPerUser,
            Name = Localizer["Max Comments Per Day Per User"],
            Description = Localizer["Rate limiting: maximum number of comments a user can post in a single day."],
            Type = SettingType.Number,
            DefaultValue = "10"
        }
    };
}
