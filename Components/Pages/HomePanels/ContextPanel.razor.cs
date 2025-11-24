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
using System.Web;

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
        [Inject]
        private BrowserService BrowserService { get; set; } = null!;


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
        private MarkupString _previewContentMarkup;
        private string _previewSearchTerm = string.Empty;
        private int _currentPreviewMatchIndex = 0;
        private int _totalPreviewMatches = 0;
        private bool _isInitialPreviewSearch = true;
        
        private bool _includePromptInCopy = true;
        private bool _includeSystemPrompt = true;

        private List<ContextListItem> _sortedFiles = new();
        private string _currentSortKey = "path";
        private bool _isSortAscending = true;
        private string? _lastInteractionPath;
        
        private const string AdoFilePrefix = "[ADO]";
        private Dictionary<string, double> _semanticScores = new();
        private string? _promptForLastSemanticSort;
        
        private bool _isClarificationDialogVisible = false;

        private string _clarificationDialogText = string.Empty;
        
        private bool _isBrowserMode = false;

        private record ContextListItem(string RelativePath, string DisplayPath, string FileName, long Size, double SemanticScore);

        private bool IsIndexReady => EmbeddingIndexService.GetIndex()?.Any() ?? false;

        protected override void OnInitialized()
        {
            AppState.SelectedFilesForContext.CollectionChanged += OnSelectedFilesChanged;
            AppState.PropertyChanged += OnAppStateChanged;
            
            BrowserService.OnContentExtracted += HandleBrowserContentExtracted;
            BrowserService.OnCloseBrowser += HandleBrowserClosed;

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
            
            bool isRangeSelect = e.ShiftKey || (e.CtrlKey && e.AltKey);
            bool isMultiSelect = e.CtrlKey && !e.AltKey && !e.ShiftKey;

            if (isRangeSelect && !string.IsNullOrEmpty(_lastInteractionPath))
            {
                var visiblePaths = _sortedFiles.Select(f => f.RelativePath).ToList();
                var startIndex = visiblePaths.IndexOf(_lastInteractionPath);
                var endIndex = visiblePaths.IndexOf(fileRelativePath);

                if (startIndex != -1 && endIndex != -1)
                {
                    var min = Math.Min(startIndex, endIndex);
                    var max = Math.Max(startIndex, endIndex);

                    // Ha nincs lenyomva a Ctrl (csak Shift), akkor töröljük a többit (standard viselkedés).
                    // Ha Ctrl is le van nyomva (pl Ctrl+Alt), akkor hozzáadunk (additív).
                    if (!e.CtrlKey)
                    {
                        currentSelection.Clear();
                    }

                    for (int i = min; i <= max; i++)
                    {
                        var path = visiblePaths[i];
                        if (!currentSelection.Contains(path))
                        {
                            currentSelection.Add(path);
                        }
                    }
                }
            }
            else if (isMultiSelect)
            {
                if (currentSelection.Contains(fileRelativePath))
                {
                    currentSelection.Remove(fileRelativePath);
                }
                else
                {
                    currentSelection.Add(fileRelativePath);
                }
                _lastInteractionPath = fileRelativePath;
            }
            else 
            {
                if (currentSelection.Count == 1 && currentSelection[0] == fileRelativePath)
                {
                    currentSelection.Clear();
                    _lastInteractionPath = null;
                }
                else
                {
                    currentSelection.Clear();
                    currentSelection.Add(fileRelativePath);
                    _lastInteractionPath = fileRelativePath;
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
            await ClearPreviewSearch();
            
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
                    UpdatePreviewMarkup();
                    StateHasChanged();
                    return;
                }
                else
                {
                    _previewContent = "";
                    UpdatePreviewMarkup();
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
                    _previewContent = await File.ReadAllTextAsync(fullPath);
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

            UpdatePreviewMarkup();
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
                catch { }
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
                _includeSystemPrompt, 
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

        #region Browser Mode

        private async Task OpenAiStudioBrowser()
        {
            await CopyToClipboard();
            
            _isBrowserMode = true;
            StateHasChanged();
            BrowserService.OpenBrowser("https://aistudio.google.com/");
        }

        private void CloseBrowserMode()
        {
            _isBrowserMode = false;
            BrowserService.CloseBrowser();
            StateHasChanged();
        }

        private void HandleBrowserClosed()
        {
            _isBrowserMode = false;
            InvokeAsync(StateHasChanged);
        }

        private async Task HandleBrowserContentExtracted(string ignoredContent)
        {
            CloseBrowserMode();
            await ProcessChangesFromClipboardAsync();
        }

        #endregion
        
        #region Clarification Dialog
        
        private async Task ShowClarificationDialog(string content)
        {
            _clarificationDialogText = content;
            _isClarificationDialogVisible = true;
            StateHasChanged();
        }

        private void OnClarificationDialogClose()
        {
            _isClarificationDialogVisible = false;
            _clarificationDialogText = string.Empty;
            StateHasChanged();
        }
        
        private async Task HandleGenerateRefinedPrompt(string qaString)
        {
            OnClarificationDialogClose(); 
            AppState.ShowLoading("Részletes prompt összeállítása a vágólapra...");
            try
            {
                var sortedPaths = _sortedFiles.Select(f => f.RelativePath);
                var originalContext = await ContextProcessingService.BuildContextForClipboardAsync(
                    true, 
                    true, 
                    sortedPaths);

                var sb = new StringBuilder();
                sb.AppendLine("--- EREDETI KONTEXTUS ÉS TISZTÁZOTT RÉSZLETEK ---");
                sb.AppendLine();
                sb.AppendLine(originalContext);
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("--- TISZTÁZANDÓ KÉRDÉSEK ÉS VÁLASZOK ---");
                sb.AppendLine();
                sb.AppendLine(qaString);
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("--- UTASÍTÁS ---");
                sb.AppendLine("A fenti, kiegészített kontextus alapján készíts egy új, részletes és egyértelmű végleges promptot a fejlesztő számára. Az új promptnak tartalmaznia kell minden releváns információt a válaszokból. Ha egy kérdésre nem érkezett válasz, dönts a legjobb belátásod szerint, és jelezd a feltételezésedet egy [FELTÉTELEZÉS] taggel. Az új, végleges promptot önmagában, mindenféle magyarázat vagy előzetes szöveg nélkül add meg. Csak a végleges prompt tartalma szerepeljen a válaszodban.");

                await Clipboard.SetTextAsync(sb.ToString());
                AppState.StatusText = "Részletes prompt a vágólapra másolva. Illessze be az LLM-be a végleges prompt generálásához.";
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        #endregion

        #region Diff Processing & Automatic Routing

        private bool IsQuestionResponse(string text)
        {
            return Regex.IsMatch(text, @"\[Q\d+\]");
        }

        private async Task RouteResponseAsync(string responseContent)
        {
            if (IsQuestionResponse(responseContent))
            {
                AppState.StatusText = "Tisztázó kérdések észlelve. Dialógus megnyitása...";
                await ShowClarificationDialog(responseContent);
            }
            else
            {
                var diffArgs = await ContextProcessingService.ProcessChangesFromClipboardAsync(responseContent);

                if (!diffArgs.DiffResults.Any())
                {
                    await JSRuntime.InvokeVoidAsync("alert", "A válasz nem tartalmazott feldolgozható kódot és kérdéseket sem.\n\nNyers válasz:\n" + (responseContent.Length > 500 ? responseContent.Substring(0, 500) + "..." : responseContent));
                    AppState.StatusText = "A válasz nem értelmezhető kódként vagy kérdésként.";
                }
                else
                {
                    AppState.StatusText = $"{diffArgs.DiffResults.Count} fájl feldolgozva. Változások ablak megnyitva.";
                    await OnShowDiffDialog.InvokeAsync(diffArgs);
                }
            }
        }

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

            AppState.ShowLoading("Vágólap tartalmának elemzése (Routing)...");
            await Task.Delay(1);
            try
            {
                await RouteResponseAsync(clipboardText);
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
                
                var responseContent = await OpenRouterService.GenerateContentAsync(sortedPaths);
                
                await RouteResponseAsync(responseContent);
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

        #region Preview Search
        public async Task SearchInPreview(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                await ClearPreviewSearch();
                return;
            }

            _previewSearchTerm = searchTerm;
            _isInitialPreviewSearch = true;
            UpdatePreviewMarkup();
            StateHasChanged();
            
            await Task.Delay(10); 
            await ScrollToCurrentPreviewMatch();
        }
        
        private void UpdatePreviewMarkup()
        {
            if (string.IsNullOrEmpty(_previewContent))
            {
                _previewContentMarkup = new MarkupString("");
                ResetPreviewSearchState();
                return;
            }

            if (string.IsNullOrWhiteSpace(_previewSearchTerm))
            {
                _previewContentMarkup = new MarkupString(HttpUtility.HtmlEncode(_previewContent));
                ResetPreviewSearchState();
                return;
            }

            var term = _previewSearchTerm;
            int matchCount = 0;
            var encodedContent = HttpUtility.HtmlEncode(_previewContent);
            var encodedTerm = HttpUtility.HtmlEncode(term);

            _totalPreviewMatches = Regex.Matches(encodedContent, Regex.Escape(encodedTerm), RegexOptions.IgnoreCase).Count;

            if (_isInitialPreviewSearch)
            {
                _currentPreviewMatchIndex = _totalPreviewMatches > 0 ? 1 : 0;
                _isInitialPreviewSearch = false;
            }

            if (_totalPreviewMatches > 0 && (_currentPreviewMatchIndex == 0 || _currentPreviewMatchIndex > _totalPreviewMatches))
            {
                _currentPreviewMatchIndex = 1;
            }
            else if (_totalPreviewMatches == 0)
            {
                _currentPreviewMatchIndex = 0;
            }

            var highlightedContent = Regex.Replace(encodedContent,
                Regex.Escape(encodedTerm),
                m =>
                {
                    string classStr = "highlight";
                    if (matchCount == _currentPreviewMatchIndex - 1)
                    {
                        classStr += " current";
                    }
                    var result = $"<span id=\"preview-match-{matchCount}\" class=\"{classStr}\">{m.Value}</span>";
                    matchCount++;
                    return result;
                },
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            _previewContentMarkup = new MarkupString(highlightedContent);
        }

        private void ResetPreviewSearchState()
        {
            _totalPreviewMatches = 0;
            _currentPreviewMatchIndex = 0;
        }

        private async Task HandlePreviewSearchKeyup(KeyboardEventArgs e)
        {
            _isInitialPreviewSearch = true;
            if (e.Key == "Enter")
            {
                await FindNextInPreview();
            }
            else
            {
                UpdatePreviewMarkup();
                if (_totalPreviewMatches > 0 && _currentPreviewMatchIndex > 0)
                {
                    await ScrollToCurrentPreviewMatch();
                }
            }
        }

        private async Task FindNextInPreview()
        {
            if (_totalPreviewMatches == 0) return;
            _isInitialPreviewSearch = false;

            _currentPreviewMatchIndex++;
            if (_currentPreviewMatchIndex > _totalPreviewMatches)
            {
                _currentPreviewMatchIndex = 1;
            }
            UpdatePreviewMarkup();
            await ScrollToCurrentPreviewMatch();
        }

        private async Task FindPreviousInPreview()
        {
            if (_totalPreviewMatches == 0) return;
            _isInitialPreviewSearch = false;

            _currentPreviewMatchIndex--;
            if (_currentPreviewMatchIndex < 1)
            {
                _currentPreviewMatchIndex = _totalPreviewMatches;
            }
            UpdatePreviewMarkup();
            await ScrollToCurrentPreviewMatch();
        }

        private async Task ClearPreviewSearch()
        {
            _previewSearchTerm = string.Empty;
            _isInitialPreviewSearch = true;
            UpdatePreviewMarkup();
            await Task.CompletedTask;
        }

        private async Task ScrollToCurrentPreviewMatch()
        {
            if (_totalPreviewMatches > 0 && _currentPreviewMatchIndex > 0)
            {
                var elementId = $"preview-match-{_currentPreviewMatchIndex - 1}";
                await JSRuntime.InvokeVoidAsync("scrollToElementInContainer", "preview-box", elementId);
            }
        }
        #endregion

        public void Dispose()
        {
            AppState.SelectedFilesForContext.CollectionChanged -= OnSelectedFilesChanged;
            AppState.PropertyChanged -= OnAppStateChanged;
            
            if (BrowserService != null)
            {
                BrowserService.OnContentExtracted -= HandleBrowserContentExtracted;
                BrowserService.OnCloseBrowser -= HandleBrowserClosed;
            }
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