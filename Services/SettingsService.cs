using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class SettingsService
    {
        private const string SettingsFileName = ".llm_context_collector_settings.json";
        private readonly JsonStorageService _storage;
        private Settings? _settingsCache;

        public SettingsService(JsonStorageService storage)
        {
            _storage = storage;
        }

        public async Task<Settings> GetSettingsAsync()
        {
            if (_settingsCache != null)
            {
                return _settingsCache;
            }
            _settingsCache = await _storage.ReadFromFileAsync<Settings>(SettingsFileName) ?? new Settings();
            return _settingsCache;
        }

        public async Task SaveSettingsAsync(Settings settings)
        {
            _settingsCache = settings;
            await _storage.WriteToFileAsync(SettingsFileName, settings);
        }
    }
}