using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class HistoryService
    {
        private const string HistoryFileName = ".llm_context_collector_history.json";
        private const int HistoryLimit = 30;

        private readonly JsonStorageService _storage;
        private readonly AppState _appState;

        public HistoryService(JsonStorageService storage, AppState appState)
        {
            _storage = storage;
            _appState = appState;
        }

        public async Task LoadHistoryAsync()
        {
            var history = await _storage.ReadFromFileAsync<List<HistoryEntry>>(HistoryFileName);
            _appState.HistoryEntries = history ?? new List<HistoryEntry>();
            _appState.NotifyStateChanged(nameof(AppState.HistoryEntries));
        }

        public async Task SaveCurrentStateAsync()
        {
            if (string.IsNullOrEmpty(_appState.ProjectRoot) || !_appState.SelectedFilesForContext.Any())
            {
                return;
            }

            var currentState = new HistoryEntry
            {
                Timestamp = DateTime.Now,
                RootFolder = _appState.ProjectRoot,
                SelectedFiles = _appState.SelectedFilesForContext.ToList(),
                ExtensionsFilter = string.Join(",", _appState.ExtensionFilters.Where(kvp => kvp.Value).Select(kvp => kvp.Key)),
                IgnoreFilter = _appState.IgnorePatternsRaw,
                PromptText = _appState.PromptText,
                SelectedTemplateTitle = _appState.PromptTemplates
                                           .FirstOrDefault(p => p.Id == _appState.SelectedPromptTemplateId)?.Title
            };

            var history = _appState.HistoryEntries;
            history.Insert(0, currentState);
            _appState.HistoryEntries = history.Take(HistoryLimit).ToList();

            await _storage.WriteToFileAsync(HistoryFileName, _appState.HistoryEntries);
            _appState.NotifyStateChanged(nameof(AppState.HistoryEntries));
        }
    }
}