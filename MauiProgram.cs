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

            builder.Services.AddSingleton<ITextGenerationProvider, GroqTextGenerationProvider>();
            
            // --- AI Services (ONNX-based) ---
            // These services are registered conditionally. If the ONNX model or tokenizer is not found,
            // dummy ("Null") implementations are used, allowing the app to run without AI search features.
            
            var modelDir = Path.Combine(FileSystem.AppDataDirectory, "models");
            Directory.CreateDirectory(modelDir);
            var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
            var modelPath = Path.Combine(modelDir, "model_quantized.onnx");

            TryCopyAsset("tokenizer.json", tokenizerPath);
            TryCopyAsset("model_quantized.onnx", modelPath);
            TryCopyAsset("model_quantized.onnx_data", Path.Combine(modelDir, "model_quantized.onnx_data"));

            if (File.Exists(modelPath) && File.Exists(tokenizerPath))
            {
                try
                {
                    // Register real AI services if models are found
                    var tokenizer = new Tokenizer(tokenizerPath);
                    builder.Services.AddSingleton(tokenizer);
                    builder.Services.AddSingleton<IChunker, TokenizerChunker>();
                    builder.Services.AddSingleton<IEmbeddingProvider>(sp =>
                        new EmbeddingGemmaOnnxProvider(
                            onnxPath: modelPath,
                            tokenizer: sp.GetRequiredService<Tokenizer>(),
                            maxLen: 2048,
                            useDml: true,
                            threads: 1
                        )
                    );
                }
                catch (Exception ex)
                {
                    // If loading fails, fall back to dummy services
                    System.Diagnostics.Debug.WriteLine($"Error initializing ONNX models, AI features will be disabled: {ex.Message}");
                    builder.Services.AddSingleton<IChunker, NullChunker>();
                    builder.Services.AddSingleton<IEmbeddingProvider, NullEmbeddingProvider>();
                }
            }
            else
            {
                // Register dummy services if model/tokenizer files are missing
                builder.Services.AddSingleton<IChunker, NullChunker>();
                builder.Services.AddSingleton<IEmbeddingProvider, NullEmbeddingProvider>();
            }

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