using Microsoft.Extensions.Logging;
using LlmContextCollector.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using LlmContextCollector.AI.Embeddings;
using LlmContextCollector.AI.Search;
using LlmContextCollector.AI;
using System.Net.Http.Headers;
using LlmContextCollector.AI.Embeddings.Chunking;

#if WINDOWS
using LlmContextCollector.WinUI.Services;
#endif

namespace LlmContextCollector
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

#if WINDOWS
                        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                "--enable-features=OverlayScrollbar");
#endif

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            builder.Services.AddSingleton<AppState>();
            builder.Services.AddSingleton<AiLogService>();
            builder.Services.AddSingleton<JsonStorageService>();
            builder.Services.AddSingleton<HistoryService>();
            builder.Services.AddSingleton<HistoryManagerService>();
            builder.Services.AddSingleton<PromptService>();
            builder.Services.AddSingleton<SettingsService>();
            builder.Services.AddSingleton<ProjectSettingsService>();
            builder.Services.AddTransient<FileSystemService>();

            builder.Services.AddTransient<FileTreeFilterService>();
            builder.Services.AddTransient<ReferenceFinderService>();
            builder.Services.AddSingleton<EmbeddingIndexService>();
            builder.Services.AddTransient<RelevanceFinderService>();
            builder.Services.AddSingleton<GitSuggestionService>();
            builder.Services.AddSingleton<GitService>();
            builder.Services.AddTransient<CodeStructureExtractor>();
            builder.Services.AddSingleton<AzureDevOpsService>();
            builder.Services.AddTransient<LlmResponseParserService>();
            builder.Services.AddTransient<ContextProcessingService>();
            builder.Services.AddSingleton<GitWorkflowService>();
            builder.Services.AddTransient<ProjectService>();
            builder.Services.AddTransient<FileContextService>();
            builder.Services.AddTransient<QueryBuilders>();
            builder.Services.AddTransient<OllamaService>();
            builder.Services.AddSingleton<BrowserService>();
            
            // --- Új szolgáltatások regisztrálása ---
            builder.Services.AddTransient<AgentContentLoader>();
            builder.Services.AddTransient<AgentPromptBuilder>();


#if WINDOWS
            builder.Services.AddSingleton<IFolderPickerService, FolderPickerService>();
#endif
            builder.Services.AddSingleton(Clipboard.Default);

            builder.Services.AddHttpClient("groq", c =>
            {
                c.Timeout = TimeSpan.FromSeconds(120);
                c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            });
            
            builder.Services.AddHttpClient("AzureDevOps");

            builder.Services.AddHttpClient("OllamaClient", (sp, c) =>
            {
                var appState = sp.GetRequiredService<AppState>();
                c.BaseAddress = new Uri(appState.OllamaApiUrl);
                c.Timeout = TimeSpan.FromMinutes(10); 
            });

            // Külön HttpClient az embeddingnek, hogy ne akadjon össze a chat-tel, ha más timeout kellene
            builder.Services.AddHttpClient("OllamaEmbed", (sp, c) =>
            {
                 c.Timeout = TimeSpan.FromMinutes(5);
            });

            builder.Services.AddSingleton<ITextGenerationProvider, GroqTextGenerationProvider>();
            
            // --- Embedding Provider Setup ---

            builder.Services.AddSingleton<NullEmbeddingProvider>();
            builder.Services.AddSingleton<OllamaEmbeddingProvider>();
            builder.Services.AddSingleton<IChunker, SimpleChunker>();
            builder.Services.AddSingleton<IEmbeddingProvider, SwitchingEmbeddingProvider>();

            builder.Services.AddSingleton(new JsonEmbeddingCache(Path.Combine(FileSystem.AppDataDirectory, "embeddings", "cache.json")));
            builder.Services.AddSingleton<SemanticSearchService>();

            return builder.Build();
        }
    }
}