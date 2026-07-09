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
        // ── AI: Chat / Translation (3 settings) ──────────────────────────────────
        new GlobalSettingDefinition
        {
            Key = OpenAiInstance,
            Name = Localizer["OpenAI Chat Endpoint"],
            Description = Localizer["The OpenAI-compatible chat completions endpoint used for document translation. Must be the full URL including /v1/chat/completions, e.g. https://ollama.example.com/v1/chat/completions or https://api.openai.com/v1/chat/completions. Unrelated to embedding/vector search."],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = OpenAiLocalizationModel,
            Name = Localizer["Localization Model"],
            Description = Localizer["The LLM model name used for translating documents, e.g. qwen3.5:27b-q8_0, gpt-4o, or deepseek-chat. Must be available at the OpenAI Chat Endpoint above. Unrelated to embedding/vector search."],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = OpenAiApiToken,
            Name = Localizer["OpenAI API Token"],
            Description = Localizer["The bearer token for authenticating with the OpenAI Chat Endpoint, e.g. sk-abc123... or 5a0fbdefa19f.... Leave empty if the endpoint does not require authentication."],
            Type = SettingType.Text,
            DefaultValue = ""
        },

        // ── AI: Embedding / Vector Search (3 settings) ────────────────────────────
        new GlobalSettingDefinition
        {
            Key = EmbeddingOllamaInstance,
            Name = Localizer["Embedding Endpoint"],
            Description = Localizer["The Ollama API base URL used specifically for generating document and query embeddings (vector search). Only the host is used — /api/embed is appended automatically. Falls back to the OpenAI Chat Endpoint when empty. E.g. https://ollama.example.com"],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingModel,
            Name = Localizer["Embedding Model"],
            Description = Localizer["The embedding model name for vector search, e.g. bge-m3:latest. Must be available at the Embedding Endpoint. Only used for vector search, not for translation."],
            Type = SettingType.Text,
            DefaultValue = "bge-m3:latest"
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingApiToken,
            Name = Localizer["Embedding API Token"],
            Description = Localizer["The bearer token for authenticating with the Embedding Endpoint, e.g. 5a0fbdefa19f.... Falls back to OpenAI API Token when empty."],
            Type = SettingType.Text,
            DefaultValue = ""
        },

        // ── AI: Feature switch ────────────────────────────────────────────────────
        new GlobalSettingDefinition
        {
            Key = EnableEmbeddingBasedSearch,
            Name = Localizer["Enable Embedding-Based Search"],
            Description = Localizer["Master switch for semantic (vector-based) search. When enabled and the embedding model is configured, search results display a green \"Search based on AI (Vector Database)\" badge and use cosine similarity ranking. When disabled or not configured, search silently falls back to keyword matching. Requires Embedding Endpoint and Embedding Model."],
            Type = SettingType.Bool,
            DefaultValue = "False"
        },

        // ── Localization ──────────────────────────────────────────────────────────
        new GlobalSettingDefinition
        {
            Key = LocalizationLanguages,
            Name = Localizer["Localization Languages"],
            Description = Localizer["Comma-separated BCP-47 language codes to translate documents into, e.g. en-US,ja-JP,ko-KR,fr-FR,zh-CN. Leave empty or set to a single language to disable AI translation."],
            Type = SettingType.Text,
            DefaultValue = "en-US,en-GB,zh-TW,zh-HK,ja-JP,ko-KR,vi-VN,th-TH,de-DE,fr-FR,es-ES,ru-RU,it-IT,pt-PT,pt-BR,ar-SA,nl-NL,sv-SE,pl-PL,tr-TR,ro-RO,da-DK,uk-UA,id-ID,fi-FI,hi-IN,el-GR"
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingQueryCacheLimit,
            Name = Localizer["Embedding Query Cache Limit"],
            Description = Localizer["Maximum number of cached query embeddings in the database. When exceeded, the least recently accessed entries are evicted (LRU). Default 2000. Adjust based on your database capacity and expected query diversity."],
            Type = SettingType.Number,
            DefaultValue = "2000"
        },

        // ── Rate Limiting ─────────────────────────────────────────────────────────
        new GlobalSettingDefinition
        {
            Key = MaxCommentsPerDayPerUser,
            Name = Localizer["Max Comments Per Day Per User"],
            Description = Localizer["The maximum number of comments a user can post per day."],
            Type = SettingType.Number,
            DefaultValue = "10"
        }
    };
}
