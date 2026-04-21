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
    public partial class ContextTab : ComponentBase, IDisposable
    {
        [Inject] private AppState AppState { get; set; } = null!;
        [Inject] private PromptService PromptService { get; set; } = null!;
        [Inject] private IClipboard Clipboard { get; set; } = null!;
        [Inject] private IJSRuntime JSRuntime { get; set; } = null!;
        [Inject] private GitSuggestionService GitSuggestionService { get; set; } = null!;
        [Inject] private GitService GitService { get; set; } = null!;
        [Inject] private GitWorkflowService GitWorkflowService { get; set; } = null!;
        [Inject] private ContextProcessingService ContextProcessingService { get; set; } = null!;
        [Inject] private ReferenceFinderService ReferenceFinderService { get; set; } = null!;
        [Inject] private ChatService ChatService { get; set; } = null!;
        [Inject] private LocalizationService LocalizationService { get; set; } = null!;
        [Inject] private AzureDevOpsService SettingsStore { get; set; } = null!;
        [Inject] private BuildManagerService BuildManagerService { get; set; } = null!;
        [Inject] private AiProviderFactory ProviderFactory { get; set; } = null!;
        [Inject] private ProjectSettingsService ProjectSettingsService { get; set; } = null!;
        [Inject] private IImageClipboardService ImageClipboardService { get; set; } = null!;

        [Parameter] public EventCallback<MouseEventArgs> OnShowListContextMenu { get; set; }
        [Parameter] public List<string> SelectedItems { get; set; } = new();
        [Parameter] public EventCallback<List<string>> SelectedItemsChanged { get; set; }
        [Parameter] public EventCallback OnShowPromptManager { get; set; }
        [Parameter] public EventCallback OnShowSettingsDialog { get; set; }
        [Parameter] public EventCallback<DiffResultArgs> OnShowDiffDialog { get; set; }
        [Parameter] public EventCallback OnAddSelectedToContext { get; set; }
        [Parameter] public EventCallback OnExcludeSelectedFromTree { get; set; }
        [Parameter] public EventCallback OnRequestRemoveSelected { get; set; }
        [Parameter] public EventCallback OnHistorySaveRequested { get; set; }
        [Parameter] public EventCallback<(MouseEventArgs, string)> OnSplitterMouseDown { get; set; }
        [Parameter] public EventCallback<DiffResultArgs> OnRequestLocalizationPath { get; set; }

        [CascadingParameter] public LlmContextCollector.Components.Pages.Home? HomeRef { get; set; }

        private long _charCount = 0;
        private long _tokenCount = 0;
        private string _previewContent = string.Empty;
        private MarkupString _previewContentMarkup;
        private string _contextSearchTerm = string.Empty;
        private int _currentPreviewMatchIndex = 0;
        private int _totalPreviewMatches = 0;
        private bool _isInitialPreviewSearch = true;
        private DotNetObjectReference<ContextTab>? _objRef;
        private string _copyButtonText = "Másolás";
        private bool _isBottomDropdownOpen = false;
        private bool _isDocsDropdownOpen = false;
        private List<ContextListItem> _sortedFiles = new();
        private string _currentSortKey = "path";
        private bool _isSortAscending = true;
        private string? _lastInteractionPath;
        private int? _adoWorkItemIdToLoad;
        private bool _isClarificationDialogVisible = false;
        private string _clarificationDialogText = string.Empty;
        private Dictionary<string, long> _originalSizeCache = new();

        private record ContextListItem(string RelativePath, string DisplayPath, string FileName, long Size);

        protected override async Task OnInitializedAsync()
        {
            _objRef = DotNetObjectReference.Create(this);
            AppState.SelectedFilesForContext.CollectionChanged += OnSelectedFilesChanged;
            AppState.PropertyChanged += OnAppStateChanged;
            UpdateSortedFiles();
            SortFiles();
            await UpdateCountsAsync();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                await JSRuntime.InvokeVoidAsync("initializePreviewInteractions", _objRef);
            }
        }

        private async void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppState.AdoDocsExist) || e.PropertyName == nameof(AppState.ActiveGlobalPromptId) || e.PropertyName == nameof(AppState.PromptTemplates))
            {
                await InvokeAsync(StateHasChanged);
            }
            else if (e.PropertyName == nameof(AppState.CurrentPreviewPath))
            {
                await InvokeAsync(async () =>
                {
                    await UpdatePreview();
                    if (!string.IsNullOrEmpty(AppState.CurrentPreviewPath) && _sortedFiles.Any(f => f.RelativePath == AppState.CurrentPreviewPath))
                    {
                        await ScrollToPath(AppState.CurrentPreviewPath);
                    }
                });
            }
            else if (e.PropertyName == nameof(AppState.PreviewSearchTerm))
            {
                await InvokeAsync(() =>
                {
                    UpdatePreviewMarkup();
                    StateHasChanged();
                });
            }
        }

        private void OnSelectedFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _ = UpdateCountsAsync();
            UpdateSortedFiles();
            SortFiles();
            InvokeAsync(StateHasChanged);
        }

        public async Task ScrollToPath(string relPath)
        {
            if (_sortedFiles.Any(f => f.RelativePath == relPath))
            {
                await Task.Delay(10);
                await JSRuntime.InvokeVoidAsync("scrollToElement", GetRowId(relPath));
            }
        }

        private string GetRowId(string relPath) => "ctxrow-" + Convert.ToBase64String(Encoding.UTF8.GetBytes(relPath)).Replace("=", "").Replace("+", "-").Replace("/", "_");

        private async Task HandleListKeyDown(KeyboardEventArgs e)
        {
            if (e.Key == "Delete") await OnRequestRemoveSelected.InvokeAsync();
        }

        private async Task OnAddReferencesClick(string filePath)
        {
            if (string.IsNullOrEmpty(AppState.ProjectRoot)) return;
            AppState.ShowLoading("Referenciák keresése...");
            try
            {
                var newFiles = await ReferenceFinderService.FindReferencesAsync(new List<string> { filePath }, AppState.FileTree, AppState.ProjectRoot, 1);
                int added = 0;
                foreach (var f in newFiles)
                {
                    if (!AppState.SelectedFilesForContext.Contains(f)) { AppState.SelectedFilesForContext.Add(f); added++; }
                }
                AppState.SaveContextListState();
                AppState.StatusText = $"{added} referencia hozzáadva.";
            }
            finally { AppState.HideLoading(); }
        }

        private void OnRemoveItemClick(string filePath)
        {
            if (AppState.SelectedFilesForContext.Contains(filePath))
            {
                AppState.SelectedFilesForContext.Remove(filePath);
                AppState.SaveContextListState();
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
                    if (!e.CtrlKey) currentSelection.Clear();
                    for (int i = min; i <= max; i++)
                    {
                        var path = visiblePaths[i];
                        if (!currentSelection.Contains(path)) currentSelection.Add(path);
                    }
                }
            }
            else if (isMultiSelect)
            {
                if (currentSelection.Contains(fileRelativePath)) currentSelection.Remove(fileRelativePath);
                else currentSelection.Add(fileRelativePath);
                _lastInteractionPath = fileRelativePath;
            }
            else
            {
                currentSelection.Clear();
                currentSelection.Add(fileRelativePath);
                _lastInteractionPath = fileRelativePath;
            }
            await SelectedItemsChanged.InvokeAsync(currentSelection);
        }

        private async Task ShowContextMenuForRow(MouseEventArgs e, string fileRelativePath)
        {
            if (!SelectedItems.Contains(fileRelativePath)) await SelectedItemsChanged.InvokeAsync(new List<string> { fileRelativePath });
            await OnShowListContextMenu.InvokeAsync(e);
        }

        public async Task UpdatePreview(string? path = null, List<string>? items = null)
        {
            var effectiveSelection = items ?? SelectedItems;
            string? fileRelPath = path ?? (!string.IsNullOrEmpty(AppState.CurrentPreviewPath) ? AppState.CurrentPreviewPath : (effectiveSelection.Count == 1 ? effectiveSelection.First() : null));

            if (fileRelPath == null)
            {
                _previewContent = effectiveSelection.Count > 1 ? $"{effectiveSelection.Count} fájl kijelölve." : "";
                UpdatePreviewMarkup();
                StateHasChanged();
                return;
            }

            try
            {
                if (fileRelPath == "[LOCALIZATIONS]")
                {
                    _previewContent = await ContextProcessingService.GetAggregatedLocalizationsAsync(AppState.SelectedFilesForContext);
                }
                else if (fileRelPath.StartsWith("[ORIGINAL]"))
                {
                    var purePath = fileRelPath.Substring(10);
                    var devBranch = await GitWorkflowService.GetDevelopmentBranchNameAsync();
                    _previewContent = await GitService.GetFileContentAtBranchAsync(devBranch, purePath);
                }
                else
                {
                    string fullPath = fileRelPath.StartsWith("[ADO]")
                        ? Path.Combine(AppState.AdoDocsPath ?? string.Empty, fileRelPath.Substring(5))
                        : Path.Combine(AppState.ProjectRoot ?? string.Empty, fileRelPath.Replace('/', Path.DirectorySeparatorChar));

                    _previewContent = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath) : "Fájl nem található.";
                }
            }
            catch (Exception ex) { _previewContent = $"Hiba: {ex.Message}"; }
            UpdatePreviewMarkup();
            StateHasChanged();
        }

        private async Task UpdateCountsAsync()
        {
            long currentChars = 0;
            if (string.IsNullOrEmpty(AppState.ProjectRoot)) return;

            string? devBranch = null;

            foreach (var fileRelPath in AppState.SelectedFilesForContext)
            {
                if (fileRelPath.StartsWith("[ORIGINAL]"))
                {
                    if (_originalSizeCache.TryGetValue(fileRelPath, out long sz)) currentChars += sz;
                    else
                    {
                        devBranch ??= await GitWorkflowService.GetDevelopmentBranchNameAsync();
                        var content = await GitService.GetFileContentAtBranchAsync(devBranch, fileRelPath.Substring(10));
                        _originalSizeCache[fileRelPath] = content.Length;
                        currentChars += content.Length;
                    }
                }
                else
                {
                    string fullPath = fileRelPath.StartsWith("[ADO]") ? Path.Combine(AppState.AdoDocsPath, fileRelPath.Substring(5)) : Path.Combine(AppState.ProjectRoot, fileRelPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(fullPath)) currentChars += new FileInfo(fullPath).Length;
                }
            }

            UpdateSortedFiles();
            SortFiles();
            _charCount = currentChars;
            _tokenCount = _charCount / 4;
            await InvokeAsync(StateHasChanged);
        }

        private void UpdateSortedFiles()
        {
            _sortedFiles.Clear();
            foreach (var fileRelPath in AppState.SelectedFilesForContext)
            {
                if (!string.IsNullOrWhiteSpace(_contextSearchTerm) && !fileRelPath.Contains(_contextSearchTerm, StringComparison.OrdinalIgnoreCase)) continue;
                string fileName = Path.GetFileName(fileRelPath);
                string displayPath = fileRelPath.StartsWith("[ORIGINAL]") ? fileRelPath.Substring(10) : fileRelPath;
                long size = 0;
                if (fileRelPath.StartsWith("[ORIGINAL]")) _originalSizeCache.TryGetValue(fileRelPath, out size);
                else
                {
                    string fullPath = fileRelPath.StartsWith("[ADO]") ? Path.Combine(AppState.AdoDocsPath, fileRelPath.Substring(5)) : Path.Combine(AppState.ProjectRoot, fileRelPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(fullPath)) size = new FileInfo(fullPath).Length;
                }
                _sortedFiles.Add(new ContextListItem(fileRelPath, displayPath, fileName, size));
            }
        }

        private void SortFiles()
        {
            _sortedFiles = _currentSortKey switch
            {
                "name" => _isSortAscending ? _sortedFiles.OrderBy(f => f.FileName).ToList() : _sortedFiles.OrderByDescending(f => f.FileName).ToList(),
                "size" => _isSortAscending ? _sortedFiles.OrderBy(f => f.Size).ToList() : _sortedFiles.OrderByDescending(f => f.Size).ToList(),
                _ => _isSortAscending ? _sortedFiles.OrderBy(f => f.RelativePath).ToList() : _sortedFiles.OrderByDescending(f => f.RelativePath).ToList()
            };
        }

        private async Task SortBy(string key) { if (_currentSortKey == key) _isSortAscending = !_isSortAscending; else { _currentSortKey = key; _isSortAscending = true; } SortFiles(); await InvokeAsync(StateHasChanged); }
        private string GetSortClass(string key) => _currentSortKey != key ? "" : (_isSortAscending ? "active asc" : "active desc");

        private void ClearSelectionList() { AppState.SelectedFilesForContext.Clear(); AppState.SaveContextListState(); }
        private void ClearPrompt() { AppState.PromptText = string.Empty; AppState.AttachedImages.Clear(); }

        [JSInvokable]
        public async Task OnImagePastedAsync(string base64)
        {
            try
            {
                var bytes = Convert.FromBase64String(base64.Split(',')[1]);
                var cacheDir = Path.Combine(FileSystem.CacheDirectory, "pasted_images");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                var fileName = $"Pasted_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString().Substring(0, 4)}.png";
                var path = Path.Combine(cacheDir, fileName);
                
                await File.WriteAllBytesAsync(path, bytes);
                AppState.AttachedImages.Add(new AttachedImage { FilePath = path, FileName = fileName, Base64Thumbnail = base64 });
                AppState.StatusText = "Kép beillesztve.";
                StateHasChanged();
            }
            catch (Exception ex)
            {
                AppState.StatusText = $"Hiba a kép beillesztésekor: {ex.Message}";
            }
        }

        private async Task CopyImagesAsync() 
        { 
            if (AppState.AttachedImages.Any()) 
            {
                await ImageClipboardService.CopyImagesToClipboardAsync(AppState.AttachedImages.Select(x => x.FilePath));
                AppState.StatusText = $"{AppState.AttachedImages.Count} kép a vágólapra másolva.";
            }
        }
        private async Task PickImagesAsync()
        {
            var results = await FilePicker.Default.PickMultipleAsync(PickOptions.Images);
            if (results != null)
            {
                foreach (var r in results)
                {
                    var stream = await r.OpenReadAsync();
                    var ms = new MemoryStream(); await stream.CopyToAsync(ms);
                    AppState.AttachedImages.Add(new AttachedImage { FilePath = r.FullPath, FileName = r.FileName, Base64Thumbnail = $"data:image/png;base64,{Convert.ToBase64String(ms.ToArray())}" });
                }
            }
        }

        private void ToggleBottomDropdown() { _isBottomDropdownOpen = !_isBottomDropdownOpen; _isDocsDropdownOpen = false; }
        private void ToggleDocsDropdown() { _isDocsDropdownOpen = !_isDocsDropdownOpen; _isBottomDropdownOpen = false; }
        private void SelectBottomPrompt(Guid id) { AppState.ActiveGlobalPromptId = id; _isBottomDropdownOpen = false; }
        public void CloseCustomDropdowns() { _isBottomDropdownOpen = _isDocsDropdownOpen = false; StateHasChanged(); }
        private async Task SaveProjectSettingsAsync() => await ProjectSettingsService.SaveSettingsForProjectAsync(AppState.ProjectRoot);
        private void ToggleDocumentSelection(AttachableDocument doc) { doc.IsSelected = !doc.IsSelected; _ = SaveProjectSettingsAsync(); }
        private void EditDocument(AttachableDocument doc) { _isDocsDropdownOpen = false; HomeRef?.ShowAttachableDocDialog(doc); }
        private void AddNewDocument() { _isDocsDropdownOpen = false; HomeRef?.ShowAttachableDocDialog(null); }
        private async Task HandleAdoKeyup(KeyboardEventArgs e) { if (e.Key == "Enter") await LoadAdoWorkItemAsync(); }

        private async Task LoadAdoWorkItemAsync()
        {
            if (!_adoWorkItemIdToLoad.HasValue) return;
            AppState.ShowLoading("Lekérés...");
            try
            {
                var res = await SettingsStore.GetFormattedWorkItemAsync(_adoWorkItemIdToLoad.Value, AppState.AttachedImages.Count);
                AppState.PromptText = res.Text + "\n\n" + AppState.PromptText;
                foreach (var img in res.Images) AppState.AttachedImages.Add(img);
                _adoWorkItemIdToLoad = null;

                if (res.FailedImagesCount > 0)
                {
                    AppState.StatusText = $"Work item lekérve, de {res.FailedImagesCount} kép letöltése sikertelen volt, lásd a naplót!";
                }
                else
                {
                    AppState.StatusText = "Work item sikeresen lekérve.";
                }
            }
            finally { AppState.HideLoading(); }
        }

        private async Task CopyTemplateById(Guid id) { var p = AppState.PromptTemplates.FirstOrDefault(x => x.Id == id); if (p != null) await Clipboard.SetTextAsync(p.Content); }

        private async Task CopyToClipboard()
        {
            var content = await ContextProcessingService.BuildContextForClipboardAsync(AppState.IncludePromptInCopy, AppState.IncludeSystemPromptInCopy, AppState.IncludeFilesInCopy, _sortedFiles.Select(f => f.RelativePath));
            await OnHistorySaveRequested.InvokeAsync();
            await Clipboard.SetTextAsync(content);
            _copyButtonText = "Másolva! ✓"; StateHasChanged();
            await Task.Delay(2000); _copyButtonText = "Másolás"; StateHasChanged();
        }

        public async Task RouteResponseAsync(string response)
        {
            if (Regex.IsMatch(response, @"\[Q\d+\]")) { _clarificationDialogText = response; _isClarificationDialogVisible = true; StateHasChanged(); }
            else
            {
                var args = await ContextProcessingService.ProcessChangesFromClipboardAsync(response);
                if (args.DiffResults.Any(r => r.Path.StartsWith("[LOC]")) && string.IsNullOrEmpty(AppState.LocalizationResourcePath)) await OnRequestLocalizationPath.InvokeAsync(args);
                else await OnShowDiffDialog.InvokeAsync(args);
            }
        }

        private async Task HandleKontextClick()
        {
            if (string.IsNullOrWhiteSpace(AppState.PromptText)) return;
            var input = $"KERESD MEG A RELEVÁNS FÁJLOKAT AZ ALÁBBI FELADATHOZ: {AppState.PromptText}";
            var files = await ContextProcessingService.BuildContextForClipboardAsync(false, false, true, AppState.SelectedFilesForContext);
            await ChatService.SendMessageAsync(input, "Segíts megtalálni a releváns fájlokat a projekten belül. Csak az útvonalakat sorold fel, amik érintettek lehetnek.", files, forceRefreshContext: true, clearHistory: true);
        }

        private async Task HandleAiChatClick()
        {
            if (string.IsNullOrWhiteSpace(AppState.PromptText)) return;
            var content = await ContextProcessingService.BuildContextForClipboardAsync(AppState.IncludePromptInCopy, AppState.IncludeSystemPromptInCopy, AppState.IncludeFilesInCopy, _sortedFiles.Select(f => f.RelativePath));
            var system = await PromptService.GetSystemPromptAsync();
            await ChatService.SendMessageAsync(AppState.PromptText, system, content, forceRefreshContext: true, clearHistory: true);
        }

        private async Task HandleGitDiffClick()
        {
            var args = await GitWorkflowService.PrepareGitDiffForReviewAsync(AppState.PromptText);
            await OnShowDiffDialog.InvokeAsync(args);
        }

        private async Task ProcessChangesFromClipboardAsync() { var t = await Clipboard.GetTextAsync(); if (!string.IsNullOrEmpty(t)) await RouteResponseAsync(t); }

        private async Task HandleGenerateRefinedPrompt(string qa)
        {
            _isClarificationDialogVisible = false;
            var ctx = await ContextProcessingService.BuildContextForClipboardAsync(true, true, true, _sortedFiles.Select(f => f.RelativePath));
            await Clipboard.SetTextAsync($"{ctx}\n\nQA:\n{qa}\n\nKérlek készíts végleges promptot!");
        }

        private void OnClarificationDialogClose() => _isClarificationDialogVisible = false;

        private void HandleBuildFixRequested() { AppState.PromptText = ContextProcessingService.BuildContextForBuildErrors(AppState.CurrentBuildErrors); }
        private async Task HandleErrorLocationClicked(BuildError e) { var n = AppState.FindNodeByPath(Path.IsPathRooted(e.FilePath) ? e.FilePath : Path.Combine(AppState.ProjectRoot, e.FilePath)); if (n != null) await HomeRef!.HandleNodeClick((n, null)); }

        private async Task HandleContextSearchKeyup(KeyboardEventArgs e) { UpdateSortedFiles(); SortFiles(); }
        private void ClearContextSearch() { _contextSearchTerm = ""; UpdateSortedFiles(); SortFiles(); }

        public async Task SearchInPreview(string term) { AppState.PreviewSearchTerm = term; _isInitialPreviewSearch = true; UpdatePreviewMarkup(); await ScrollToCurrentPreviewMatch(); }

        private void UpdatePreviewMarkup()
        {
            if (string.IsNullOrEmpty(_previewContent)) { _previewContentMarkup = new MarkupString(""); return; }
            var encoded = HttpUtility.HtmlEncode(_previewContent);
            
            if (!string.IsNullOrEmpty(AppState.PreviewSearchTerm))
            {
                int count = 0; 
                _totalPreviewMatches = Regex.Matches(encoded, Regex.Escape(AppState.PreviewSearchTerm), RegexOptions.IgnoreCase).Count;
                var highlighted = Regex.Replace(encoded, Regex.Escape(AppState.PreviewSearchTerm), m => 
                {
                    var isCurrent = (count == _currentPreviewMatchIndex - 1);
                    return $"<span id=\"preview-match-{count++}\" class=\"highlight {(isCurrent ? "current" : "")}\">{m.Value}</span>";
                }, RegexOptions.IgnoreCase);
                _previewContentMarkup = new MarkupString(highlighted);
            }
            else 
            {
                _totalPreviewMatches = 0;
                _previewContentMarkup = new MarkupString(encoded);
            }
        }

        private async Task FindNextInPreview() { if (_totalPreviewMatches > 0) { _currentPreviewMatchIndex = (_currentPreviewMatchIndex % _totalPreviewMatches) + 1; UpdatePreviewMarkup(); await ScrollToCurrentPreviewMatch(); } }
        private async Task FindPreviousInPreview() { if (_totalPreviewMatches > 0) { _currentPreviewMatchIndex = _currentPreviewMatchIndex <= 1 ? _totalPreviewMatches : _currentPreviewMatchIndex - 1; UpdatePreviewMarkup(); await ScrollToCurrentPreviewMatch(); } }
        private async Task ClearPreviewSearch() { AppState.PreviewSearchTerm = ""; _currentPreviewMatchIndex = 0; UpdatePreviewMarkup(); }
        private async Task ScrollToCurrentPreviewMatch() { if (_totalPreviewMatches > 0) await JSRuntime.InvokeVoidAsync("scrollToElementInContainer", "preview-box", $"preview-match-{_currentPreviewMatchIndex - 1}"); }
        private async Task HandlePreviewSearchKeyup(KeyboardEventArgs e) { if (e.Key == "Enter") await FindNextInPreview(); else { _isInitialPreviewSearch = true; UpdatePreviewMarkup(); } }

        public void Dispose() { AppState.SelectedFilesForContext.CollectionChanged -= OnSelectedFilesChanged; AppState.PropertyChanged -= OnAppStateChanged; _objRef?.Dispose(); }
    }

    public class DiffResultArgs
    {
        public string GlobalExplanation { get; }
        public List<DiffResult> DiffResults { get; }
        public string FullLlmResponse { get; }
        public string LocalizationData { get; }
        public string OriginalPrompt { get; }

        public DiffResultArgs(
            string globalExplanation,
            List<DiffResult> diffResults,
            string fullLlmResponse,
            string originalPrompt = "",
            string localizationData = "")
        {
            GlobalExplanation = globalExplanation;
            DiffResults = diffResults;
            FullLlmResponse = fullLlmResponse;
            OriginalPrompt = originalPrompt;
            LocalizationData = localizationData;
        }
    }
}