using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class PromptService
    {
        private const string PromptFileName = ".llm_context_prompts.json";
        private readonly JsonStorageService _storage;
        
        private PromptData _promptDataCache = new();

        public PromptService(JsonStorageService storage)
        {
            _storage = storage;
        }
        
        private async Task EnsureLoadedAsync()
        {
            // Cache check to avoid frequent disk I/O
            if (!_promptDataCache.Prompts.Any() && string.IsNullOrEmpty(_promptDataCache.Preferences.GlobalPrefix) && string.IsNullOrEmpty(_promptDataCache.Preferences.DeveloperPrompt))
            {
                 _promptDataCache = await _storage.ReadFromFileAsync<PromptData>(PromptFileName) ?? new PromptData();
            }
        }

        public async Task<List<PromptTemplate>> GetPromptsAsync()
        {
            await EnsureLoadedAsync();
            return _promptDataCache.Prompts.OrderBy(p => p.Title).ToList();
        }

        public async Task<string> GetGlobalPrefixAsync()
        {
            await EnsureLoadedAsync();
            return _promptDataCache.Preferences.GlobalPrefix;
        }
        
        public async Task<string> GetDeveloperPromptAsync()
        {
            await EnsureLoadedAsync();
            return _promptDataCache.Preferences.DeveloperPrompt;
        }

        public async Task SaveAllAsync(List<PromptTemplate> prompts, string globalPrefix, string developerPrompt)
        {
            _promptDataCache.Prompts = prompts;
            _promptDataCache.Preferences.GlobalPrefix = globalPrefix;
            _promptDataCache.Preferences.DeveloperPrompt = developerPrompt;

            await _storage.WriteToFileAsync(PromptFileName, _promptDataCache);
        }
    }
}