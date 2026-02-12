using LlmContextCollector.Models;
using LlmContextCollector.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace LlmContextCollector.Components.Dialogs
{
    public partial class DocumentSearchDialog : ComponentBase, IDisposable
    {
        [Parameter] public bool IsVisible { get; set; }
        [Parameter] public EventCallback OnClose { get; set; }
        [Parameter] public EventCallback<List<string>> OnAccept { get; set; }

        [Inject] private AppState AppState { get; set; } = null!;
        [Inject] private RelevanceFinderService RelevanceFinderService { get; set; } = null!;
        [Inject] private IJSRuntime JSRuntime { get; set; } = null!;

        private string _searchPrompt = string.Empty;
        private bool _isSearching = false;
        private bool _isAgentMode = false;
        private string _agentStatus = string.Empty;
        private bool _searchInProgramFiles = true;
        private bool _searchInOtherDocuments = false;

        private List<SearchResultViewModel> _searchResults = new();
        private List<ContextFileViewModel> _contextFilesForSearch = new();
        private RelevanceResult? _selectedResult;
        private string _previewContent = string.Empty;

        // Élő naplózáshoz szükséges állapot
        private class AgentLog 
        { 
            public string Type { get; set; } = ""; 
            public string Text { get; set; } = ""; 
        }
        private List<AgentLog> _agentLogs = new();
        private CancellationTokenSource? _agentCts;

        protected override void OnParametersSet()
        {
            if (IsVisible)
            {
                // Alaphelyzetbe állítás minden megnyitáskor
                _searchResults.Clear();
                _selectedResult = null;
                _previewContent = string.Empty;
                _agentLogs.Clear();
                _isAgentMode = false;
                _isSearching = false;

                _contextFilesForSearch = AppState.SelectedFilesForContext
                    .Select(f => new ContextFileViewModel { FilePath = f, IsUsedForContext = true })
                    .ToList();
            }
        }

        private async Task ExecuteSearch()
        {
            if (_isSearching) return;

            _isSearching = true;
            _isAgentMode = false;
            _searchResults.Clear();
            StateHasChanged();

            try
            {
                var results = await RelevanceFinderService.FindRelevantFilesAsync(_searchInProgramFiles, _searchInOtherDocuments);
                foreach (var r in results)
                {
                    _searchResults.Add(new SearchResultViewModel { Result = r, IsSelected = true });
                }
            }
            catch (Exception ex)
            {
                await JSRuntime.InvokeVoidAsync("alert", $"Keresési hiba: {ex.Message}");
            }
            finally
            {
                _isSearching = false;
                StateHasChanged();
            }
        }

        private async Task ExecuteAgentSearch()
        {
            if (_isSearching) return;

            _isSearching = true;
            _isAgentMode = true;
            _agentLogs.Clear();
            _agentStatus = "Fut...";
            _agentCts = new CancellationTokenSource();
            StateHasChanged();

            try
            {
                // Meghívjuk az iteratív keresőt egy callback-el, ami élőben frissíti a konzolt
                var results = await RelevanceFinderService.FindRelevantFilesIterativelyAsync(async (type, text) =>
                {
                    var existing = _agentLogs.FirstOrDefault(l => l.Type == type);
                    if (existing != null)
                    {
                        existing.Text = text;
                    }
                    else
                    {
                        _agentLogs.Add(new AgentLog { Type = type, Text = text });
                    }

                    await InvokeAsync(StateHasChanged);
                    
                    // Automatikus görgetés az aljára
                    await JSRuntime.InvokeVoidAsync("scrollToBottom", "agent-log-container");
                }, _agentCts.Token);

                _searchResults.Clear();
                foreach (var r in results)
                {
                    _searchResults.Add(new SearchResultViewModel { Result = r, IsSelected = true });
                }
                
                _agentStatus = "Kész.";
            }
            catch (OperationCanceledException)
            {
                _agentStatus = "Megszakítva.";
            }
            catch (Exception ex)
            {
                _agentStatus = "Hiba.";
                await JSRuntime.InvokeVoidAsync("alert", $"Agent hiba: {ex.Message}");
            }
            finally
            {
                _isSearching = false;
                StateHasChanged();
            }
        }

        private void SelectResult(RelevanceResult result)
        {
            _selectedResult = result;
            if (result.TopChunks != null && result.TopChunks.Any())
            {
                _previewContent = string.Join("\n\n---\n\n", result.TopChunks);
            }
            else
            {
                _previewContent = "(Nincs előnézet elérhető ehhez a találathoz)";
            }
        }

        private async Task Accept()
        {
            var selectedPaths = _searchResults
                .Where(vm => vm.IsSelected)
                .Select(vm => vm.Result.FilePath)
                .ToList();

            if (selectedPaths.Any())
            {
                await OnAccept.InvokeAsync(selectedPaths);
            }
        }

        private async Task Close()
        {
            _agentCts?.Cancel();
            await OnClose.InvokeAsync();
        }

        public void Dispose()
        {
            _agentCts?.Dispose();
        }

        private class SearchResultViewModel
        {
            public RelevanceResult Result { get; set; } = null!;
            public bool IsSelected { get; set; }
        }

        private class ContextFileViewModel
        {
            public string FilePath { get; set; } = string.Empty;
            public bool IsUsedForContext { get; set; }
        }
    }
}