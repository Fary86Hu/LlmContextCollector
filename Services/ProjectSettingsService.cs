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
                
                foreach (var kvp in settings.ExtensionFilters)
                {
                    _appState.ExtensionFilters[kvp.Key] = kvp.Value;
                }
                
                _appState.AttachableDocuments.Clear();
                if (settings.AttachableDocuments != null)
                {
                    foreach (var doc in settings.AttachableDocuments)
                    {
                        _appState.AttachableDocuments.Add(doc);
                    }
                }

                // Projekt-specifikus parancsok betöltése
                if (!string.IsNullOrWhiteSpace(settings.BuildCommand))
                {
                    _appState.DefaultBuildCommand = settings.BuildCommand;
                }
                if (!string.IsNullOrWhiteSpace(settings.RunCommand))
                {
                    _appState.DefaultRunCommand = settings.RunCommand;
            }

            _appState.TreeSearchHistory.Clear();
            if (settings.TreeSearchHistory != null)
            {
                    foreach (var item in settings.TreeSearchHistory) _appState.TreeSearchHistory.Add(item);
            }

            _appState.PreviewSearchHistory.Clear();
            if (settings.PreviewSearchHistory != null)
            {
                    foreach (var item in settings.PreviewSearchHistory) _appState.PreviewSearchHistory.Add(item);
            }

            _appState.SelectedLaunchProfile = settings.SelectedLaunchProfile ?? string.Empty;
                _appState.LocalizationResourcePath = settings.LocalizationResourcePath ?? string.Empty;

                _appState.NotifyStateChanged(nameof(AppState.ExtensionFilters));
                _appState.NotifyStateChanged(nameof(AppState.IgnorePatternsRaw));
                _appState.NotifyStateChanged(nameof(AppState.AttachableDocuments));
                _appState.NotifyStateChanged(nameof(AppState.DefaultBuildCommand));
                _appState.NotifyStateChanged(nameof(AppState.DefaultRunCommand));
            }
            else 
            {
                // Ha nincs projekt-specifikus beállítás, ürítjük a dokumentumokat, 
                // de a Build/Run parancsok maradnak a globális alapértelmezésen (amit a SettingsService töltött be).
                _appState.AttachableDocuments.Clear();
                _appState.NotifyStateChanged(nameof(AppState.AttachableDocuments));
            }
        }

        public async Task SaveSettingsForProjectAsync(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return;

            await EnsureLoadedAsync();

            var settings = new ProjectFilterSettings
            {
                IgnorePatterns = _appState.IgnorePatternsRaw,
                ExtensionFilters = new Dictionary<string, bool>(_appState.ExtensionFilters),
                AttachableDocuments = _appState.AttachableDocuments.ToList(),
                BuildCommand = _appState.DefaultBuildCommand,
                RunCommand = _appState.DefaultRunCommand,
                SelectedLaunchProfile = _appState.SelectedLaunchProfile,
                LocalizationResourcePath = _appState.LocalizationResourcePath,
                TreeSearchHistory = _appState.TreeSearchHistory.ToList(),
                PreviewSearchHistory = _appState.PreviewSearchHistory.ToList()
            };

            _allProjectSettings![projectPath] = settings;

            await _storage.WriteToFileAsync(SettingsFileName, _allProjectSettings);
        }
    }
}