using LlmContextCollector.Models;
using LlmContextCollector.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LlmContextCollector.Components.Pages.HomePanels
{
    public partial class FileTreePanel : ComponentBase, IDisposable
    {
        [Inject]
        private AppState AppState { get; set; } = null!;
        [Inject]
        private IFolderPickerService FolderPickerService { get; set; } = null!;
        [Inject]
        private FileTreeFilterService FileTreeFilterService { get; set; } = null!;
        [Inject]
        private ProjectSettingsService ProjectSettingsService { get; set; } = null!;

        [Parameter]
        public EventCallback OnRequestApplyFiltersAndReload { get; set; }


        [Parameter]
        public EventCallback<HistoryEntry> OnLoadHistoryEntry { get; set; }

        [Parameter]
        public EventCallback<MouseEventArgs> OnShowTreeContextMenu { get; set; }

        [Parameter]
        public EventCallback<(FileNode Node, MouseEventArgs Args)> OnNodeClick { get; set; }

        [Parameter]
        public EventCallback OnAzureDevOpsAttach { get; set; }

        [Parameter]
        public EventCallback OnStartIndexingCode { get; set; }

        [Parameter]

        public EventCallback OnStartIndexingAdo { get; set; }

        private Timer? _searchTimer;
        private List<FileNode> _searchResults = new();
        private int _currentSearchIndex = -1;
        private string _lastSearchTerm = string.Empty;
        private bool _lastSearchInContent = false;
        private bool _isFiltered = false;

        protected override void OnInitialized()
        {
            AppState.PropertyChanged += OnAppStateChanged;
            _searchTimer = new Timer(SearchTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
        }

        private async void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppState.FileTree))
            {
                _searchResults.Clear();
                _currentSearchIndex = -1;
                _isFiltered = false;
            }
            await InvokeAsync(StateHasChanged);
        }

        private async Task SelectProjectFolder()
        {
            var folderPath = await FolderPickerService.PickFolderAsync();
            if (!string.IsNullOrEmpty(folderPath))
            {
                AppState.ProjectRoot = folderPath;
                // Beállítások betöltése az új mappához
                await ProjectSettingsService.LoadSettingsForProjectAsync(folderPath);
                await OnRequestApplyFiltersAndReload.InvokeAsync();
            }
        }


        protected string FormatHistoryEntry(HistoryEntry entry)
        {
            var promptPreview = string.IsNullOrWhiteSpace(entry.PromptText) ? "(üres prompt)" : entry.PromptText.ReplaceLineEndings(" ").Trim();
            if (promptPreview.Length > 80) promptPreview = promptPreview.Substring(0, 80) + "...";
            return $"{entry.Timestamp:yy-MM-dd HH:mm} | {promptPreview} | {Path.GetFileName(entry.RootFolder)} ({entry.SelectedFiles.Count}f)";
        }

        protected async Task LoadSelectedHistory(ChangeEventArgs e)
        {
            if (int.TryParse(e.Value?.ToString(), out int index) && index >= 0)
            {
                var entry = AppState.HistoryEntries[index];
                await OnLoadHistoryEntry.InvokeAsync(entry);
            }
        }

        private async Task HandleSearchKeyup(KeyboardEventArgs e)
        {
            _searchTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (e.Key == "Enter" || e.Key == "ArrowDown")
            {
                await FindNext(false);
            }
            else if (e.Key == "ArrowUp")
            {
                await FindPrevious();
            }
            else
            {
                _searchTimer?.Change(1000, Timeout.Infinite);
            }
        }

        private async Task HandleNodeClick((FileNode Node, MouseEventArgs Args) payload)
        {
            await OnNodeClick.InvokeAsync(payload);
        }

        private void SearchTimerCallback(object? state)
        {
            InvokeAsync(() => FindNext(true));
        }
        
        private async Task FilterTree()
        {
            _searchTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            await FileTreeFilterService.FilterFileTreeAsync();
            _isFiltered = !string.IsNullOrWhiteSpace(AppState.SearchTerm);
            StateHasChanged();
        }

        private async Task PopulateSearchResults()
        {
            if (string.IsNullOrWhiteSpace(AppState.SearchTerm))
            {
                _searchResults.Clear();
                return;
            }

            _lastSearchTerm = AppState.SearchTerm;
            _lastSearchInContent = AppState.SearchInContent;
            _searchResults.Clear();
            _currentSearchIndex = -1;

            AppState.ShowLoading($"Keresés: '{_lastSearchTerm}'...");
            await Task.Delay(1);

            try
            {
                var term = _lastSearchTerm.ToLowerInvariant();
                var nodesToSearch = new List<FileNode>();

                void CollectSearchableNodes(IEnumerable<FileNode> nodes)
                {
                    foreach (var node in nodes)
                    {
                        if (_isFiltered && !node.IsVisible)
                        {
                            continue;
                        }
                        
                        nodesToSearch.Add(node);

                        if (node.IsDirectory)
                        {
                            CollectSearchableNodes(node.Children);
                        }
                    }
                }

                CollectSearchableNodes(AppState.FileTree);

                foreach (var node in nodesToSearch)
                {
                    bool isMatch = node.Name.ToLowerInvariant().Contains(term);

                    if (!isMatch && AppState.SearchInContent && !node.IsDirectory)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(node.FullPath);
                            if (fileInfo.Length < 5 * 1024 * 1024) // 5MB limit
                            {
                                 var content = await File.ReadAllTextAsync(node.FullPath);
                                 if (content.ToLowerInvariant().Contains(term))
                                 {
                                     isMatch = true;
                                 }
                            }
                        }
                        catch { /* ignore read errors */ }
                    }

                    if (isMatch)
                    {
                        _searchResults.Add(node);
                    }
                }
                
                _searchResults = _searchResults.OrderBy(n => n.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
                AppState.StatusText = $"{_searchResults.Count} találat.";
            }
            finally
            {
                AppState.HideLoading();
            }
        }


        private async Task FindNext(bool isNewSearch)
        {
            _searchTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            bool searchParametersChanged = AppState.SearchTerm != _lastSearchTerm || AppState.SearchInContent != _lastSearchInContent;
            
            if (isNewSearch || searchParametersChanged || !_searchResults.Any())
            {
                await PopulateSearchResults();
            }

            if (!_searchResults.Any()) return;

            _currentSearchIndex++;
            if (_currentSearchIndex >= _searchResults.Count)
            {
                _currentSearchIndex = 0;
            }
            await SelectSearchResult(_currentSearchIndex);
        }

        private async Task FindPrevious()
        {
            _searchTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (AppState.SearchTerm != _lastSearchTerm || AppState.SearchInContent != _lastSearchInContent || !_searchResults.Any())
            {
                await PopulateSearchResults();
                _currentSearchIndex = 0; 
            }

            if (!_searchResults.Any()) return;

            _currentSearchIndex--;
            if (_currentSearchIndex < 0)
            {
                _currentSearchIndex = _searchResults.Count - 1;
            }
            await SelectSearchResult(_currentSearchIndex);
        }

        private async Task SelectSearchResult(int index)
        {
            if (index < 0 || index >= _searchResults.Count) return;
            var nodeToSelect = _searchResults[index];
            await OnNodeClick.InvokeAsync((nodeToSelect, null));
        }
        
        private async Task ToggleFilter()
        {
            _searchTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (_isFiltered)
            {
                await ClearFilter(preserveSearchTerm: true);
            }
            else
            {
                await FilterTree();
            }
        }

        private async Task ClearSearch()
        {
            _searchTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            await ClearFilter(preserveSearchTerm: false);
        }

        private async Task ClearFilter(bool preserveSearchTerm)
        {
            string? selectedNodePath = GetSelectedNodePath();

            if (!preserveSearchTerm)
            {
                AppState.SearchTerm = "";
            }
            
            _searchResults.Clear();
            _currentSearchIndex = -1;
            _lastSearchTerm = AppState.SearchTerm;

            if (_isFiltered)
            {
                FileTreeFilterService.ClearFileTreeFilter();
                _isFiltered = false;
            }

            await ReselectNode(selectedNodePath);
        }
        
        private string? GetSelectedNodePath()
        {
            if (_currentSearchIndex >= 0 && _currentSearchIndex < _searchResults.Count)
            {
                var lastSearched = _searchResults[_currentSearchIndex];
                if (lastSearched.IsSelectedInTree) return lastSearched.FullPath;
            }

            var selectedNodes = new List<FileNode>();
            FindSelectedNodes(AppState.FileTree, selectedNodes);

            var firstSelected = selectedNodes.FirstOrDefault();
            return firstSelected?.FullPath;
        }

        private void FindSelectedNodes(IEnumerable<FileNode> nodes, List<FileNode> selected)
        {
            foreach (var node in nodes)
            {
                if (node.IsSelectedInTree)
                {
                    selected.Add(node);
                }
                if (node.Children.Any())
                {
                    FindSelectedNodes(node.Children, selected);
                }
            }
        }

        private async Task ReselectNode(string? nodePath)
        {
            if (nodePath != null)
            {
                var nodeToReselect = AppState.FindNodeByPath(nodePath);
                if (nodeToReselect != null)
                {
                    await OnNodeClick.InvokeAsync((nodeToReselect, null));
                }
            }
            StateHasChanged();
        }

        public void Dispose()
        {
            AppState.PropertyChanged -= OnAppStateChanged;
            _searchTimer?.Dispose();
        }
    }
}