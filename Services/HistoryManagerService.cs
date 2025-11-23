using LlmContextCollector.Models;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LlmContextCollector.Services
{
    public class HistoryManagerService
    {
        private readonly AppState _appState;
        private readonly ProjectService _projectService;
        private readonly ProjectSettingsService _projectSettingsService;

        public HistoryManagerService(AppState appState, ProjectService projectService, ProjectSettingsService projectSettingsService)
        {
            _appState = appState;
            _projectService = projectService;
            _projectSettingsService = projectSettingsService;
        }
        
        public async Task ApplyHistoryEntryAsync(HistoryEntry entry)
        {
            _appState.ShowLoading($"Előzmény betöltése: {Path.GetFileName(entry.RootFolder)}...");
            await Task.Delay(1);
            try
            {
                _appState.ProjectRoot = entry.RootFolder;
                _appState.IgnorePatternsRaw = entry.IgnoreFilter;

                var extensions = entry.ExtensionsFilter.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).ToHashSet();

                foreach (var ext in extensions)
                {
                    _appState.AddExtensionFilter(ext);
                }

                var currentExts = _appState.ExtensionFilters.Keys.ToList();
                var extensionStateChanged = false;
                foreach (var key in currentExts)
                {
                    var shouldBeEnabled = extensions.Contains(key);
                    if (_appState.ExtensionFilters[key] != shouldBeEnabled)
                    {
                        _appState.ExtensionFilters[key] = shouldBeEnabled;
                        extensionStateChanged = true;
                    }
                }

                // Itt töltjük be a projekt-specifikus mentett beállításokat, felülírva az előzményből származókat, ha léteznek.
                await _projectSettingsService.LoadSettingsForProjectAsync(_appState.ProjectRoot);

                if (extensionStateChanged)
                {
                    _appState.NotifyStateChanged(nameof(AppState.ExtensionFilters));
                }

                await _projectService.ReloadProjectAsync(preserveSelection: false);


                _appState.SelectedFilesForContext.Clear();
                foreach (var file in entry.SelectedFiles)
                {
                    _appState.SelectedFilesForContext.Add(file);
                }
                _appState.ResetContextListHistory();
                _appState.SaveContextListState();

                _appState.PromptText = entry.PromptText;
                var matchingTemplate = _appState.PromptTemplates.FirstOrDefault(p => p.Title == entry.SelectedTemplateTitle);
                _appState.SelectedPromptTemplateId = matchingTemplate?.Id ?? System.Guid.Empty;
                _appState.StatusText = $"Előzmény betöltve: {Path.GetFileName(entry.RootFolder)}";
            }
            finally
            {
                _appState.HideLoading();
            }
        }
    }
}