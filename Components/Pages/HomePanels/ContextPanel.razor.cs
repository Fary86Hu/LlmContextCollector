using LlmContextCollector.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using System.Collections.Specialized;
using System.Text;
using System.Text.RegularExpressions;
using LlmContextCollector.Services;
using System.IO;
using System.ComponentModel;
using System.Linq;

namespace LlmContextCollector.Components.Pages.HomePanels
{
    public partial class ContextPanel : ComponentBase, IDisposable
    {
        [Inject]
        private AppState AppState { get; set; } = null!;
        [Inject]
        private PromptService PromptService { get; set; } = null!;
        [Inject]
        private RelevanceFinderService RelevanceFinder { get; set; } = null!;
        [Inject]
        private IClipboard Clipboard { get; set; } = null!;
        [Inject]
        private IJSRuntime JSRuntime { get; set; } = null!;
        [Inject]
        private EmbeddingIndexService EmbeddingIndexService { get; set; } = null!;
        [Inject]
        private GitSuggestionService GitSuggestionService { get; set; } = null!;
        [Inject]
        private GitService GitService { get; set; } = null!;
        [Inject]
        private GitWorkflowService GitWorkflowService { get; set; } = null!;
        [Inject]
        private ContextProcessingService ContextProcessingService { get; set; } = null!;

        [Parameter]
        public EventCallback<MouseEventArgs> OnShowListContextMenu { get; set; }

        [Parameter]
        public List<string> SelectedItems { get; set; } = new();

        [Parameter]
        public EventCallback<List<string>> SelectedItemsChanged { get; set; }

        [Parameter]
        public EventCallback OnShowPromptManager { get; set; }

        [Parameter]
        public EventCallback OnShowSettingsDialog { get; set; }

        [Parameter]
        public EventCallback<RelevanceResultArgs> OnShowRelevanceDialog { get; set; }

        [Parameter]
        public EventCallback<DiffResultArgs> OnShowDiffDialog { get; set; }

        [Parameter]
        public EventCallback OnRequestRemoveSelected { get; set; }

        [Parameter]
        public EventCallback OnHistorySaveRequested { get; set; }

        [Parameter]
        public EventCallback OnStartIndexingRequested { get; set; }
        
        [Parameter]
        public EventCallback OnAzureDevOpsAttach { get; set; }
        
        [Parameter]
        public EventCallback<(MouseEventArgs, string)> OnSplitterMouseDown { get; set; }

        private long _charCount = 0;
        private long _tokenCount = 0;
        private string _previewContent = string.Empty;
        private bool _includePromptInCopy = true;
        private bool _includeGlobalPrefixInCopy = true;

        private List<ContextListItem> _sortedFiles = new();
        private string _currentSortKey = "path";
        private bool _isSortAscending = true;
        
        private const string AdoFilePrefix = "[ADO]";

        private record ContextListItem(string RelativePath, string DisplayPath, string FileName, long Size);

        private bool IsIndexReady => EmbeddingIndexService.GetIndex()?.Any() ?? false;

        protected override void OnInitialized()
        {
            AppState.SelectedFilesForContext.CollectionChanged += OnSelectedFilesChanged;
            AppState.PropertyChanged += OnAppStateChanged;
            UpdateSortedFiles();
        }

        private async void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppState.IsSemanticIndexBuilding) ||
                e.PropertyName == nameof(AppState.AdoDocsExist))
            {
                await InvokeAsync(StateHasChanged);
            }
        }

        private void OnSelectedFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateCounts();
            UpdateSortedFiles();
            InvokeAsync(StateHasChanged);
        }

        private async Task HandleListKeyDown(KeyboardEventArgs e)
        {
            if (e.Key == "Delete")
            {
                await OnRequestRemoveSelected.InvokeAsync();
            }
        }
        
        private async Task OnFileRowClick(MouseEventArgs e, string fileRelativePath)
        {
            var currentSelection = SelectedItems.ToList();
    
            if (e.CtrlKey)
            {
                if (currentSelection.Contains(fileRelativePath))
                {
                    currentSelection.Remove(fileRelativePath);
                }
                else
                {
                    currentSelection.Add(fileRelativePath);
                }
            }
            else 
            {
                if (currentSelection.Count == 1 && currentSelection[0] == fileRelativePath)
                {
                    currentSelection.Clear();
                }
                else
                {
                    currentSelection.Clear();
                    currentSelection.Add(fileRelativePath);
                }
            }
    
            await SelectedItemsChanged.InvokeAsync(currentSelection);
        }

        private async Task ShowContextMenuForRow(MouseEventArgs e, string fileRelativePath)
        {
            if (!SelectedItems.Contains(fileRelativePath))
            {
                await SelectedItemsChanged.InvokeAsync(new List<string> { fileRelativePath });
                await Task.Delay(1); 
            }
            await OnShowListContextMenu.InvokeAsync(e);
        }

        public async Task UpdatePreview(string? path = null)
        {
            string? fileRelPath = path;
            if (fileRelPath == null)
            {
                if (SelectedItems.Count == 1)
                {
                    fileRelPath = SelectedItems.First();
                }
                else if (SelectedItems.Count > 1)
                {
                    _previewContent = $"{SelectedItems.Count} fájl kiválasztva. Válassz egyet az előnézethez.";
                    StateHasChanged();
                    return;
                }
                else
                {
                    _previewContent = "";
                    StateHasChanged();
                    return;
                }
            }

            string fullPath;
            if (fileRelPath.StartsWith(AdoFilePrefix))
            {
                fullPath = Path.Combine(AppState.AdoDocsPath, fileRelPath.Substring(AdoFilePrefix.Length));
            }
            else
            {
                fullPath = Path.Combine(AppState.ProjectRoot ?? "", fileRelPath.Replace('/', Path.DirectorySeparatorChar));
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    using var reader = new StreamReader(fullPath);
                    var buffer = new char[10000];
                    var charsRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                    _previewContent = new string(buffer, 0, charsRead);
                    if (charsRead == 10000)
                    {
                        _previewContent += "\n\n[... Fájl vége levágva az előnézetben ...]";
                    }
                }
                else
                {
                    _previewContent = $"Fájl nem található: {fullPath}";
                }
            }
            catch (Exception ex)
            {
                _previewContent = $"Hiba a fájl olvasásakor: {ex.Message}";
            }

            StateHasChanged();
        }

        private void UpdateCounts()
        {
            long currentChars = 0;
            if (string.IsNullOrEmpty(AppState.ProjectRoot))
            {
                _tokenCount = 0;
                _charCount = 0;
                return;
            }

            foreach (var fileRelPath in AppState.SelectedFilesForContext)
            {
                try
                {
                    string fullPath;
                    if (fileRelPath.StartsWith(AdoFilePrefix))
                    {
                        fullPath = Path.Combine(AppState.AdoDocsPath, fileRelPath.Substring(AdoFilePrefix.Length));
                    }
                    else
                    {
                        fullPath = Path.Combine(AppState.ProjectRoot, fileRelPath.Replace('/', Path.DirectorySeparatorChar));
                    }

                    if (File.Exists(fullPath))
                    {
                        var fileInfo = new FileInfo(fullPath);
                        currentChars += fileInfo.Length;
                    }
                }
                catch { /* Ignore errors for counting */ }
            }
            _charCount = currentChars;
            _tokenCount = _charCount > 0 ? _charCount / 4 : 0;
        }

        private void UpdateSortedFiles()
        {
            _sortedFiles.Clear();
            if (string.IsNullOrEmpty(AppState.ProjectRoot) && !AppState.AdoDocsExist) return;

            foreach (var fileRelPath in AppState.SelectedFilesForContext)
            {
                try
                {
                    if (fileRelPath.StartsWith(AdoFilePrefix))
                    {
                        var fileName = fileRelPath.Substring(AdoFilePrefix.Length);
                        var fullPath = Path.Combine(AppState.AdoDocsPath, fileName);
                        if (File.Exists(fullPath))
                        {
                            var fileInfo = new FileInfo(fullPath);
                            _sortedFiles.Add(new ContextListItem(fileRelPath, fileRelPath, fileName, fileInfo.Length));
                        }
                        else
                        {
                            _sortedFiles.Add(new ContextListItem(fileRelPath, fileRelPath, fileName, 0));
                        }
                    }
                    else if (!string.IsNullOrEmpty(AppState.ProjectRoot))
                    {
                        var fullPath = Path.Combine(AppState.ProjectRoot, fileRelPath.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(fullPath))
                        {
                            var fileInfo = new FileInfo(fullPath);
                            _sortedFiles.Add(new ContextListItem(fileRelPath, fileRelPath, Path.GetFileName(fileRelPath), fileInfo.Length));
                        }
                        else
                        {
                            _sortedFiles.Add(new ContextListItem(fileRelPath, fileRelPath, Path.GetFileName(fileRelPath), 0));
                        }
                    }
                }
                catch
                {
                    _sortedFiles.Add(new ContextListItem(fileRelPath, fileRelPath, Path.GetFileName(fileRelPath), 0));
                }
            }
            SortFiles();
        }
        
        private async Task SortBy(string key)
        {
            if (_currentSortKey == key)
            {
                _isSortAscending = !_isSortAscending;
            }
            else
            {
                _currentSortKey = key;
                _isSortAscending = true;
            }
            SortFiles();
            await InvokeAsync(StateHasChanged);
        }

        private void SortFiles()
        {
            IOrderedEnumerable<ContextListItem> sorted;
            switch (_currentSortKey)
            {
                case "name":
                    sorted = _isSortAscending
                        ? _sortedFiles.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                        : _sortedFiles.OrderByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase);
                    break;
                case "size":
                    sorted = _isSortAscending
                        ? _sortedFiles.OrderBy(f => f.Size)
                        : _sortedFiles.OrderByDescending(f => f.Size);
                    break;
                case "path":
                default:
                    sorted = _isSortAscending
                        ? _sortedFiles.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                        : _sortedFiles.OrderByDescending(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
                    break;
            }
            _sortedFiles = sorted.ToList();
        }

        private string GetSortClass(string key)
        {
            if (_currentSortKey != key) return "";
            return _isSortAscending ? "active asc" : "active desc";
        }

        protected void ClearSelectionList()
        {
            if (AppState.SelectedFilesForContext.Any())
            {
                AppState.SelectedFilesForContext.Clear();
                AppState.SaveContextListState();
                AppState.StatusText = "A kontextus lista törölve.";
            }
        }
        
        private async Task CopyToClipboard()
        {
            var sortedPaths = _sortedFiles.Select(f => f.RelativePath);
            var content = await ContextProcessingService.BuildContextForClipboardAsync(
                _includePromptInCopy, 
                _includeGlobalPrefixInCopy, 
                sortedPaths);

            if (string.IsNullOrWhiteSpace(content))
            {
                AppState.StatusText = "Nincs másolható tartalom (se fájl, se prompt).";
                return;
            }

            await OnHistorySaveRequested.InvokeAsync();
            await Clipboard.SetTextAsync(content);
            AppState.StatusText = $"Tartalom másolva ({_charCount} kar., ~{_tokenCount} token). Előzmény mentve.";
        }

        private async Task CopyPromptOnly()
        {
            if (!string.IsNullOrWhiteSpace(AppState.PromptText))
            {
                await Clipboard.SetTextAsync(AppState.PromptText);
                AppState.StatusText = "Prompt a vágólapra másolva.";
            }
            else
            {
                AppState.StatusText = "Nincs prompt a másoláshoz.";
            }
        }


        #region Diff Processing

        private async Task ProcessGitDiffAsync()
        {
            if (string.IsNullOrWhiteSpace(AppState.ProjectRoot) || !AppState.IsGitRepository)
            {
                await JSRuntime.InvokeVoidAsync("alert", "A jelenlegi projekt mappa nem egy Git repository.");
                return;
            }

            AppState.ShowLoading("Git különbségek lekérdezése...");
            await Task.Delay(1);

            try
            {
                var diffArgs = await GitWorkflowService.PrepareGitDiffForReviewAsync();
                if (diffArgs.DiffResults.Any())
                {
                    await OnShowDiffDialog.InvokeAsync(diffArgs);
                }
            }
            catch (Exception ex)
            {
                await JSRuntime.InvokeVoidAsync("alert", $"Hiba a git parancs futtatása közben: {ex.Message}");
                AppState.StatusText = "Hiba a Git diff futtatása közben.";
            }
            finally
            {
                AppState.HideLoading();
            }
        }
        
        private async Task ProcessChangesFromClipboardAsync()
        {
            var clipboardText = await Clipboard.GetTextAsync();
            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                AppState.StatusText = "A vágólap üres vagy nem érhető el.";
                return;
            }

            if (string.IsNullOrWhiteSpace(AppState.ProjectRoot))
            {
                await JSRuntime.InvokeVoidAsync("alert", "Nincs projekt mappa kiválasztva a módosítások ellenőrzéséhez.");
                return;
            }

            AppState.ShowLoading("Vágólap tartalmának elemzése...");
            await Task.Delay(1);
            try
            {
                var diffArgs = await ContextProcessingService.ProcessChangesFromClipboardAsync(clipboardText);

                if (!diffArgs.DiffResults.Any())
                {
                    await JSRuntime.InvokeVoidAsync("alert", "Nem sikerült fájl-változásokat találni a vágólap tartalmában.");
                    AppState.StatusText = "Kész. Nem található feldolgozható fájlblokk.";
                    return;
                }
                
                await OnShowDiffDialog.InvokeAsync(diffArgs);
                AppState.StatusText = $"{diffArgs.DiffResults.Count} fájl feldolgozva. Változások ablak megnyitva.";
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        #endregion

        #region Relevance Finder

        private async Task StartManualIndexing()
        {
            await OnStartIndexingRequested.InvokeAsync();
        }

        private void StartIndexingAdo()
        {
            EmbeddingIndexService.StartBuildingAdoIndex();
        }

        private async Task FindRelevantFiles()
        {
            if (string.IsNullOrWhiteSpace(AppState.ProjectRoot))
            {
                await JSRuntime.InvokeVoidAsync("alert", "Nincs projekt mappa kiválasztva.");
                return;
            }
            if (string.IsNullOrWhiteSpace(AppState.PromptText) && !AppState.SelectedFilesForContext.Any())
            {
                await JSRuntime.InvokeVoidAsync("alert", "Nincs kontextus a kereséshez. Válassz ki legalább egy fájlt vagy írj a prompt szerkesztőbe.");
                return;
            }

            AppState.ShowLoading("Kontextus elemzése és releváns fájlok keresése...");
            await Task.Delay(1);
            try
            {
                var relevanceResults = await RelevanceFinder.FindRelevantFilesAsync();
                if (!relevanceResults.Any())
                {
                    AppState.StatusText = "Nem található új releváns fájl.";
                    await JSRuntime.InvokeVoidAsync("alert", "Nem találtunk új, a kontextushoz kapcsolódó fájlt.");
                    return;
                }

                await OnShowRelevanceDialog.InvokeAsync(new RelevanceResultArgs(relevanceResults));
                AppState.StatusText = $"{relevanceResults.Count} releváns fájl javasolva.";
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        #endregion

        public void Dispose()
        {
            AppState.SelectedFilesForContext.CollectionChanged -= OnSelectedFilesChanged;
            AppState.PropertyChanged -= OnAppStateChanged;
        }
    }

    public class RelevanceResultArgs
    {
        public List<RelevanceResult> Results { get; }
        public RelevanceResultArgs(List<RelevanceResult> results) => Results = results;
    }

    public class DiffResultArgs
    {
        public string GlobalExplanation { get; }
        public List<DiffResult> DiffResults { get; }
        public DiffResultArgs(string globalExplanation, List<DiffResult> diffResults)
        {
            GlobalExplanation = globalExplanation;
            DiffResults = diffResults;
        }
    }
}