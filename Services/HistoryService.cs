using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class HistoryService
    {
        private const string HistoryFileName = ".llm_context_collector_history.json";

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
            if (history != null)
            {
                foreach(var h in history)
                {
                    if (h.Id == Guid.Empty) h.Id = Guid.NewGuid();
                }
            }
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
                Id = Guid.NewGuid(),
                Timestamp = DateTime.Now,
                RootFolder = _appState.ProjectRoot,
                SelectedFiles = _appState.SelectedFilesForContext.ToList(),
                ExtensionsFilter = string.Join(",", _appState.ExtensionFilters.Where(kvp => kvp.Value).Select(kvp => kvp.Key)),
                IgnoreFilter = _appState.IgnorePatternsRaw,
                PromptText = _appState.PromptText,
                SelectedTemplateTitle = _appState.PromptTemplates
                                           .FirstOrDefault(p => p.Id == _appState.ActiveGlobalPromptId)?.Title
            };

            var history = _appState.HistoryEntries;
            history.Insert(0, currentState);

            var projectEntries = history.Where(e => e.RootFolder == _appState.ProjectRoot).ToList();
            if (projectEntries.Count > 20)
            {
                var toRemove = projectEntries.Skip(20).ToList();
                foreach(var rm in toRemove) history.Remove(rm);
            }

            if (history.Count > 200)
            {
                history = history.Take(200).ToList();
            }

            _appState.HistoryEntries = history;

            await _storage.WriteToFileAsync(HistoryFileName, _appState.HistoryEntries);
            _appState.NotifyStateChanged(nameof(AppState.HistoryEntries));
        }

        public async Task DeleteHistoryForProjectAsync(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return;

            var history = _appState.HistoryEntries.ToList();
            history.RemoveAll(e => e.RootFolder == projectRoot);

            _appState.HistoryEntries = history;
            _appState.NotifyStateChanged(nameof(AppState.HistoryEntries));

            await _storage.WriteToFileAsync(HistoryFileName, _appState.HistoryEntries);

            if (_appState.ProjectRoot == projectRoot)
            {
                _appState.ProjectRoot = string.Empty;
                _appState.SelectedFilesForContext.Clear();
                _appState.ResetContextListHistory();
                _appState.SetFileTree(new List<FileNode>());
                _appState.CurrentGitBranch = string.Empty;
                _appState.AvailableGitBranches.Clear();
            }
        }
    }
}