using System;
using LlmContextCollector.Models;
using LlmContextCollector.Services;

namespace LlmContextCollector.AI
{
    public class AiProviderFactory
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppState _appState;
        private readonly AppLogService _logService;

        public AiProviderFactory(IHttpClientFactory httpClientFactory, AppState appState, AppLogService logService)
        {
            _httpClientFactory = httpClientFactory;
            _appState = appState;
            _logService = logService;
        }

        public ITextGenerationProvider GetProvider(Guid configId)
        {
            AiModelConfig? config = null;
            
            if (configId != Guid.Empty)
            {
                config = _appState.AiModels.FirstOrDefault(m => m.Id == configId);
            }

            if (config == null)
            {
                config = _appState.AiModels.FirstOrDefault();
            }

            if (config == null)
            {
                throw new InvalidOperationException("Nincs konfigurált AI modell. Kérjük, adjon hozzá egyet a beállításokban!");
            }

            return config.ProviderType switch
            {
                AiProviderType.Ollama => new OllamaProvider(_httpClientFactory.CreateClient("OllamaClient"), config, _logService),
                AiProviderType.OpenAiCompatible => new OpenAiCompatibleProvider(_httpClientFactory.CreateClient("GenericAi"), config, _logService),
                AiProviderType.Gemini => new GeminiProvider(_httpClientFactory.CreateClient("GenericAi"), config, _logService),
                _ => throw new NotSupportedException($"A szolgáltató típus nem támogatott: {config.ProviderType}")
            };
        }
    }
}