using LlmContextCollector.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LlmContextCollector.Services
{
    public class ProjectSettingsService
    {
        private const string SettingsFileName = ".llm_context_project_settings.json";
        private readonly JsonStorageService _storage;
        private readonly AppState _appState;
        
        // Cache a betöltött beállításoknak: ProjektÚtvonal -> Beállítások
        private Dictionary<string, ProjectFilterSettings>? _allProjectSettings;

        public ProjectSettingsService(JsonStorageService storage, AppState appState)
        {
            _storage = storage;
            _appState = appState;
        }

        private async Task EnsureLoadedAsync()
        {
            if (_allProjectSettings == null)
            {
                _allProjectSettings = await _storage.ReadFromFileAsync<Dictionary<string, ProjectFilterSettings>>(SettingsFileName) 
                                      ?? new Dictionary<string, ProjectFilterSettings>();
            }
        }

        public async Task LoadSettingsForProjectAsync(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return;

            await EnsureLoadedAsync();

            if (_allProjectSettings!.TryGetValue(projectPath, out var settings))
            {
                _appState.IgnorePatternsRaw = settings.IgnorePatterns;
                
                // Merge extension filters: 
                // Megtartjuk a jelenlegieket is (ha esetleg van olyan, ami a mentésben nincs),
                // de felülírjuk a mentett értékekkel.
                foreach (var kvp in settings.ExtensionFilters)
                {
                    _appState.ExtensionFilters[kvp.Key] = kvp.Value;
                }
                _appState.NotifyStateChanged(nameof(AppState.ExtensionFilters));
                _appState.NotifyStateChanged(nameof(AppState.IgnorePatternsRaw));
            }
        }

        public async Task SaveSettingsForProjectAsync(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return;

            await EnsureLoadedAsync();

            var settings = new ProjectFilterSettings
            {
                IgnorePatterns = _appState.IgnorePatternsRaw,
                ExtensionFilters = new Dictionary<string, bool>(_appState.ExtensionFilters)
            };

            _allProjectSettings![projectPath] = settings;

            await _storage.WriteToFileAsync(SettingsFileName, _allProjectSettings);
        }
    }
}