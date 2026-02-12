using Microsoft.Extensions.Logging;
using LlmContextCollector.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using LlmContextCollector.AI.Embeddings;
using LlmContextCollector.AI.Search;
using LlmContextCollector.AI;
using System.Net.Http.Headers;
using LlmContextCollector.AI.Embeddings.Chunking;
using Tokenizers.DotNet;

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
            builder.Services.AddTransient<OpenRouterService>();
            builder.Services.AddTransient<OllamaService>();
            builder.Services.AddSingleton<BrowserService>();


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

            builder.Services.AddHttpClient("OpenRouter", c =>
            {
                c.BaseAddress = new Uri("https://openrouter.ai/api/v1/");
                c.Timeout = TimeSpan.FromSeconds(180);
                c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            });

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
            
            var modelDir = Path.Combine(FileSystem.AppDataDirectory, "models");
            Directory.CreateDirectory(modelDir);
            var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
            var modelPath = Path.Combine(modelDir, "model_quantized.onnx");

            TryCopyAsset("tokenizer.json", tokenizerPath);
            TryCopyAsset("model_quantized.onnx", modelPath);
            TryCopyAsset("model_quantized.onnx_data", Path.Combine(modelDir, "model_quantized.onnx_data"));

            // Regisztráljuk a Null providert mindig
            builder.Services.AddSingleton<NullEmbeddingProvider>();
            
            // Regisztráljuk az Ollama providert mindig
            builder.Services.AddSingleton<OllamaEmbeddingProvider>();

            // Regisztráljuk az ONNX providert, ha elérhetőek a fájlok
            bool onnxAvailable = false;
            if (File.Exists(modelPath) && File.Exists(tokenizerPath))
            {
                try
                {
                    var tokenizer = new Tokenizer(tokenizerPath);
                    builder.Services.AddSingleton(tokenizer);
                    builder.Services.AddSingleton<IChunker, TokenizerChunker>();
                    
                    // Önmagában regisztráljuk a konkrét típust
                    builder.Services.AddSingleton<EmbeddingGemmaOnnxProvider>(sp =>
                        new EmbeddingGemmaOnnxProvider(
                            onnxPath: modelPath,
                            tokenizer: sp.GetRequiredService<Tokenizer>(),
                            maxLen: 2048,
                            useDml: true,
                            threads: 1
                        )
                    );
                    onnxAvailable = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error initializing ONNX models: {ex.Message}");
                    builder.Services.AddSingleton<IChunker, NullChunker>();
                }
            }
            else
            {
                // Ha nincsenek meg az ONNX fájlok, egy egyszerű karakter-alapú darabolót használunk
                builder.Services.AddSingleton<IChunker, SimpleChunker>();
            }

            // A fő IEmbeddingProvider interfész a SwitchingEmbeddingProvider-re mutat
            builder.Services.AddSingleton<IEmbeddingProvider, SwitchingEmbeddingProvider>();

            // ---------------------------------

            builder.Services.AddSingleton(new JsonEmbeddingCache(Path.Combine(FileSystem.AppDataDirectory, "embeddings", "cache.json")));
            builder.Services.AddSingleton<SemanticSearchService>();

            return builder.Build();
        }

        static void TryCopyAsset(string assetName, string targetPath)
        {
            if (File.Exists(targetPath)) return;
            try
            {
                using var src = FileSystem.OpenAppPackageFileAsync(assetName).GetAwaiter().GetResult();
                using var dst = File.Create(targetPath);
                src.CopyTo(dst);
            }
            catch
            {
            }
        }
    }
}