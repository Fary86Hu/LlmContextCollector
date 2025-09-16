using LlmContextCollector.Models;
using LlmContextCollector.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using System.ComponentModel;
using System.IO;
using System.Linq;
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

        [Parameter]
        public EventCallback OnRequestApplyFiltersAndReload { get; set; }

        [Parameter]
        public EventCallback<HistoryEntry> OnLoadHistoryEntry { get; set; }
        
        [Parameter]
        public EventCallback<MouseEventArgs> OnShowTreeContextMenu { get; set; }

        [Parameter]
        public EventCallback<(FileNode Node, MouseEventArgs Args)> OnNodeClick { get; set; }

        protected override void OnInitialized()
        {
            AppState.PropertyChanged += OnAppStateChanged;
        }

        private async void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
        {

                await InvokeAsync(StateHasChanged);
            
        }

        private async Task SelectProjectFolder()
        {
            var folderPath = await FolderPickerService.PickFolderAsync();
            if (!string.IsNullOrEmpty(folderPath))
            {
                AppState.ProjectRoot = folderPath;
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
            if (e.Key == "Enter")
            {
                await FilterTree();
            }
        }
        
        private async Task FilterTree()
        {
            await FileTreeFilterService.FilterFileTreeAsync();
            StateHasChanged();
        }

        private void ClearSearch()
        {
            AppState.SearchTerm = "";
            AppState.SearchInContent = false;
            FileTreeFilterService.ClearFileTreeFilter();
            StateHasChanged();
        }
        
        private async Task HandleNodeClick((FileNode Node, MouseEventArgs Args) payload)
        {
            // The selection logic is now centralized in the parent Home component.
            // This component just notifies the parent about the click event.
            await OnNodeClick.InvokeAsync(payload);
        }
        
        public void Dispose()
        {
            AppState.PropertyChanged -= OnAppStateChanged;
        }
    }
}