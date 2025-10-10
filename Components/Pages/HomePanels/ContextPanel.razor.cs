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
using LlmContextCollector.AI;

namespace LlmContextCollector.Components.Pages.HomePanels
{
    public partial class ContextPanel : ComponentBase, IDisposable
    {
        [Inject]
        private AppState AppState { get; set; } = null!;
        [Inject]
        private PromptService PromptService { get; set; } = null!;
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
        [Inject]
        private RelevanceFinderService RelevanceFinderService { get; set; } = null!;
        [Inject]
        private OpenRouterService OpenRouterService { get; set; } = null!;


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
        public EventCallback<DiffResultArgs> OnShowDiffDialog { get; set; }

        [Parameter]
        public EventCallback OnShowDocumentSearchDialog { get; set; }

        [Parameter]
        public EventCallback OnRequestRemoveSelected { get; set; }

        [Parameter]
        public EventCallback OnHistorySaveRequested { get; set; }
        
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
        private Dictionary<string, double> _semanticScores = new();
        private string? _promptForLastSemanticSort;

        private record ContextListItem(string RelativePath, string DisplayPath, string FileName, long Size, double SemanticScore);

        private bool IsIndexReady => EmbeddingIndexService.GetIndex()?.Any() ?? false;

        protected override void OnInitialized()
        {
            AppState.SelectedFilesForContext.CollectionChanged += OnSelectedFilesChanged;
            AppState.PropertyChanged += OnAppStateChanged;
            UpdateSortedFiles();
            SortFiles();
        }

        private async void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppState.PromptText))
            {
                _promptForLastSemanticSort = null;
            }
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
            SortFiles();
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
                    var score = _semanticScores.GetValueOrDefault(fileRelPath, -1.0);
                    if (fileRelPath.StartsWith(AdoFilePrefix))
                    {
                        var fileName = fileRelPath.Substring(AdoFilePrefix.Length);
                        var fullPath = Path.Combine(AppState.AdoDocsPath, fileName);
                        if (File.Exists(fullPath))
                        {
                            var fileInfo = new FileInfo(fullPath);
                            _sortedFiles.Add(new ContextListItem(fileRelPath, fileRelPath, fileName, fileInfo.Length, score));
                        }
                        else
                        {
                            _sortedFiles.Add(new ContextListItem(fileRelPath, fileRelPath, fileName, 0, score));
                        }
                    }
                    else if (!string.IsNullOrEmpty(AppState.ProjectRoot))
                    {
                        var fullPath = Path.Combine(AppState.ProjectRoot, fileRelPath.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(fullPath))
                        {
                            var fileInfo = new FileInfo(fullPath);
                            _sortedFiles.Add(new ContextListItem(fileRelPath, fileRelPath, Path.GetFileName(fileRelPath), fileInfo.Length, score));
                        }
                        else
                        {
                            _sortedFiles.Add(new ContextListItem(fileRelPath, fileRelPath, Path.GetFileName(fileRelPath), 0, score));
                        }
                    }
                }
                catch
                {
                    var score = _semanticScores.GetValueOrDefault(fileRelPath, -1.0);
                    _sortedFiles.Add(new ContextListItem(fileRelPath, fileRelPath, Path.GetFileName(fileRelPath), 0, score));
                }
            }
        }
        
        private async Task SortBy(string key)
        {
            if (key == "semantic")
            {
                if (string.IsNullOrWhiteSpace(AppState.PromptText) || !IsIndexReady)
                {
                    AppState.StatusText = "A szemantikai rendezéshez prompt és betöltött index szükséges.";
                    return;
                }

                if (_promptForLastSemanticSort != AppState.PromptText)
                {
                    AppState.ShowLoading("Szemantikai egyezés számítása...");
                    try
                    {
                        var results = await RelevanceFinderService.ScoreGivenFilesAsync(AppState.SelectedFilesForContext);
                        
                        _semanticScores.Clear();
                        foreach (var result in results)
                        {
                            _semanticScores[result.FilePath] = result.Score;
                        }
                        _promptForLastSemanticSort = AppState.PromptText;

                        _sortedFiles = _sortedFiles.Select(item => 
                            item with { SemanticScore = _semanticScores.GetValueOrDefault(item.RelativePath, -1.0) }
                        ).ToList();
                    }
                    finally
                    {
                        AppState.HideLoading();
                    }
                }
            }

            if (_currentSortKey == key)
            {
                _isSortAscending = !_isSortAscending;
            }
            else
            {
                _currentSortKey = key;
                _isSortAscending = key != "semantic";
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
                case "semantic":
                    sorted = _isSortAscending
                        ? _sortedFiles.OrderBy(f => f.SemanticScore)
                        : _sortedFiles.OrderByDescending(f => f.SemanticScore);
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

        private void ClearPrompt()
        {
            AppState.PromptText = string.Empty;
            StateHasChanged();
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
                await OnShowDiffDialog.InvokeAsync(diffArgs);
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

        private async Task ProcessWithOpenRouterAsync()
        {
            if (string.IsNullOrWhiteSpace(AppState.OpenRouterApiKey))
            {
                await JSRuntime.InvokeVoidAsync("alert", "Az OpenRouter API kulcs nincs beállítva. Kérlek, add meg a Beállítások menüben.");
                return;
            }

            AppState.ShowLoading("OpenRouter válaszára várakozás...");
            try
            {
                var sortedPaths = _sortedFiles.Select(f => f.RelativePath);
                var diffArgs = await OpenRouterService.GenerateDiffFromContextAsync(sortedPaths);

                if (!diffArgs.DiffResults.Any())
                {
                    await JSRuntime.InvokeVoidAsync("alert", "Az OpenRouter modell nem adott vissza feldolgozható fájl-változásokat.\n\nMagyarázat:\n" + diffArgs.GlobalExplanation);
                    AppState.StatusText = "Kész. Az OpenRouter nem adott vissza feldolgozható változásokat.";
                    return;
                }

                await OnShowDiffDialog.InvokeAsync(diffArgs);
                AppState.StatusText = $"{diffArgs.DiffResults.Count} fájl feldolgozva az OpenRouter-től. Változások ablak megnyitva.";
            }
            catch (Exception ex)
            {
                await JSRuntime.InvokeVoidAsync("alert", $"Hiba az OpenRouter API hívása közben: {ex.Message}");
                AppState.StatusText = "Hiba az OpenRouter API hívása közben.";
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

    public class DiffResultArgs
    {
        public string GlobalExplanation { get; }
        public List<DiffResult> DiffResults { get; }
        public string FullLlmResponse { get; }
        public DiffResultArgs(string globalExplanation, List<DiffResult> diffResults, string fullLlmResponse)
        {
            GlobalExplanation = globalExplanation;
            DiffResults = diffResults;
            FullLlmResponse = fullLlmResponse;
        }
    }
}