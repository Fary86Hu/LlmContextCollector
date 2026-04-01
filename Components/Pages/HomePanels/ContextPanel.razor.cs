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
        private ReferenceFinderService ReferenceFinderService { get; set; } = null!;
        [Inject]
        private OllamaService OllamaService { get; set; } = null!;
        [Inject]
        private BrowserService BrowserService { get; set; } = null!;
        [Inject]
        private LocalizationService LocalizationService { get; set; } = null!;
        [Inject]
        private AzureDevOpsService SettingsStore { get; set; } = null!;


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
        private string _contextSearchTerm = string.Empty;
        private int _currentPreviewMatchIndex = 0;
        private int _totalPreviewMatches = 0;
        private bool _isInitialPreviewSearch = true;
        
        private bool _showReferences = false;
        private Dictionary<string, string> _projectTypeMap = new(StringComparer.OrdinalIgnoreCase);
        private DotNetObjectReference<ContextPanel>? _objRef;

        private string _copyButtonText = "Másolás";
        private bool _isTopDropdownOpen = false;
        private bool _isBottomDropdownOpen = false;
        private bool _isDocsDropdownOpen = false;

        private List<ContextListItem> _sortedFiles = new();
        private string _currentSortKey = "path";
        private bool _isSortAscending = true;
        private string? _lastInteractionPath;
        private int? _adoWorkItemIdToLoad;
        
        private const string AdoFilePrefix = "[ADO]";
        private Dictionary<string, double> _semanticScores = new();
        private Dictionary<string, long> _originalSizeCache = new();
        private string? _promptForLastSemanticSort;
        
        private bool _isClarificationDialogVisible = false;

        private string _clarificationDialogText = string.Empty;
        
        private record ContextListItem(string RelativePath, string DisplayPath, string FileName, long Size, double SemanticScore);

        private bool IsIndexReady => EmbeddingIndexService.GetIndex()?.Any() ?? false;

        protected override async Task OnInitializedAsync()
        {
            _objRef = DotNetObjectReference.Create(this);
            AppState.SelectedFilesForContext.CollectionChanged += OnSelectedFilesChanged;
            AppState.PropertyChanged += OnAppStateChanged;
            
            BrowserService.OnContentExtracted += HandleBrowserContentExtracted;
            
            UpdateSortedFiles();
            SortFiles();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                await JSRuntime.InvokeVoidAsync("initializePreviewInteractions", _objRef);
            }
        }

        private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppState.PromptText))
            {
                _promptForLastSemanticSort = null;
            }
            if (e.PropertyName == nameof(AppState.IsSemanticIndexBuilding) ||
                e.PropertyName == nameof(AppState.AdoDocsExist) ||
                e.PropertyName == nameof(AppState.ActiveGlobalPromptId) || 
                e.PropertyName == nameof(AppState.PromptTemplates))
            {
                InvokeAsync(StateHasChanged);
            }
        }

        private void OnSelectedFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _ = UpdateCountsAsync();
            UpdateSortedFiles();
            SortFiles();
            if (_showReferences)
            {
                UpdatePreviewMarkup();
            }
            InvokeAsync(StateHasChanged);
        }

        private async Task HandleListKeyDown(KeyboardEventArgs e)
        {
            if (e.Key == "Delete")
            {
                await OnRequestRemoveSelected.InvokeAsync();
            }
        }

        private async Task OnAddReferencesClick(string filePath)
        {
            if (string.IsNullOrEmpty(AppState.ProjectRoot)) return;
            AppState.ShowLoading("Referenciák keresése...");
            try
            {
                var newFiles = await ReferenceFinderService.FindReferencesAsync(
                    new List<string> { filePath },
                    AppState.FileTree,
                    AppState.ProjectRoot,
                    1
                );

                int added = 0;
                foreach (var f in newFiles)
                {
                    if (!AppState.SelectedFilesForContext.Contains(f))
                    {
                        AppState.SelectedFilesForContext.Add(f);
                        added++;
                    }
                }
                AppState.SaveContextListState();
                AppState.StatusText = $"{added} referencia hozzáadva.";
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        private async Task OnAddReferencingClick(string filePath)
        {
            if (filePath.StartsWith("[ORIGINAL]")) return;
            if (string.IsNullOrEmpty(AppState.ProjectRoot)) return;
            AppState.ShowLoading("Hivatkozók keresése...");
            try
            {
                var newFiles = await ReferenceFinderService.FindReferencingFilesAsync(
                    new List<string> { filePath },
                    AppState.FileTree,
                    AppState.ProjectRoot
                );

                int added = 0;
                foreach (var f in newFiles)
                {
                    if (!AppState.SelectedFilesForContext.Contains(f))
                    {
                        AppState.SelectedFilesForContext.Add(f);
                        added++;
                    }
                }
                AppState.SaveContextListState();
                AppState.StatusText = $"{added} hivatkozó fájl hozzáadva.";
            }
            finally
            {
                AppState.HideLoading();
            }
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

        private string GetFullPathFromRelative(string relPath)
        {
            if (relPath.StartsWith(AdoFilePrefix))
            {
                return Path.Combine(AppState.AdoDocsPath ?? string.Empty, relPath.Substring(AdoFilePrefix.Length));
            }
            if (relPath.StartsWith("[ORIGINAL]"))
            {
                return string.Empty;
            }
            return Path.Combine(AppState.ProjectRoot ?? string.Empty, relPath.Replace('/', Path.DirectorySeparatorChar));
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

            string fullPath = GetFullPathFromRelative(fileRelPath);

            try
            {
                if (fileRelPath.StartsWith("[ORIGINAL]"))
                {
                    var purePath = fileRelPath.Substring("[ORIGINAL]".Length);
                    var devBranch = await GitWorkflowService.GetDevelopmentBranchNameAsync();
                    var content = await GitService.GetFileContentAtBranchAsync(devBranch, purePath);
                    
                    if (content.Contains("exists on disk, but not in"))
                    {
                        _previewContent = $"[ÚJ FÁJL] Ez a fájl még nem létezik a(z) '{devBranch}' ágon.";
                    }
                    else
                    {
                        _previewContent = content;
                    }
                }
                else if (File.Exists(fullPath))
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

        private async Task UpdateCountsAsync()
        {
            long currentChars = 0;
            if (string.IsNullOrEmpty(AppState.ProjectRoot))
            {
                _tokenCount = 0;
                _charCount = 0;
                await InvokeAsync(StateHasChanged);
                return;
            }

            bool needsUiRefresh = false;
            foreach (var fileRelPath in AppState.SelectedFilesForContext)
            {
                try
                {
                    if (fileRelPath.StartsWith("[ORIGINAL]"))
                    {
                        if (_originalSizeCache.TryGetValue(fileRelPath, out long cachedSize))
                        {
                            currentChars += cachedSize;
                        }
                        else
                        {
                            var purePath = fileRelPath.Substring("[ORIGINAL]".Length);
                            var devBranch = await GitWorkflowService.GetDevelopmentBranchNameAsync();
                            var content = await GitService.GetFileContentAtBranchAsync(devBranch, purePath);
                            
                            long size = 0;
                            if (!content.Contains("exists on disk, but not in"))
                            {
                                size = content.Length;
                            }
                            
                            _originalSizeCache[fileRelPath] = size;
                            currentChars += size;
                            needsUiRefresh = true;
                        }
                    }
                    else
                    {
                        string fullPath = GetFullPathFromRelative(fileRelPath);
                        if (File.Exists(fullPath))
                        {
                            currentChars += new FileInfo(fullPath).Length;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error reading size for {fileRelPath}: {ex.Message}");
                }
            }
            _charCount = currentChars;
            _tokenCount = _charCount > 0 ? _charCount / 4 : 0;

            if (needsUiRefresh)
            {
                UpdateSortedFiles();
                SortFiles();
            }

            await InvokeAsync(StateHasChanged);
        }

        private void UpdateSortedFiles()
        {
            _sortedFiles.Clear();
            if (string.IsNullOrEmpty(AppState.ProjectRoot) && !AppState.AdoDocsExist) return;

            foreach (var fileRelPath in AppState.SelectedFilesForContext)
            {
                if (!string.IsNullOrWhiteSpace(_contextSearchTerm) && 
                    !fileRelPath.Contains(_contextSearchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var score = _semanticScores.GetValueOrDefault(fileRelPath, -1.0);
                
                string fileName;
                string displayPath;
                if (fileRelPath.StartsWith(AdoFilePrefix))
                {
                    displayPath = fileRelPath;
                    fileName = fileRelPath.Substring(AdoFilePrefix.Length);
                }
                else if (fileRelPath.StartsWith("[ORIGINAL]"))
                {
                    fileName = Path.GetFileName(fileRelPath);
                    displayPath = fileRelPath.Substring("[ORIGINAL]".Length);
                }
                else
                {
                    displayPath = fileRelPath;
                    fileName = Path.GetFileName(fileRelPath);
                }

                try
                {
                    long size = 0;
                    if (fileRelPath.StartsWith("[ORIGINAL]"))
                    {
                        _originalSizeCache.TryGetValue(fileRelPath, out size);
                    }
                    else
                    {
                        string fullPath = GetFullPathFromRelative(fileRelPath);
                        size = (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath)) ? new FileInfo(fullPath).Length : 0;
                    }
                    _sortedFiles.Add(new ContextListItem(fileRelPath, displayPath, fileName, size, score));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error adding file {fileRelPath}: {ex.Message}");
                    _sortedFiles.Add(new ContextListItem(fileRelPath, displayPath, fileName, 0, score));
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
            AppState.AttachedImages.Clear();
            StateHasChanged();
        }

                [JSInvokable]
        public async Task OnCtrlB_Pressed()
        {
            await CopyImagesAsync();
        }

        [JSInvokable]
        public async Task OnImagePastedAsync(string base64DataUrl)
        {
            try
            {
                var commaIndex = base64DataUrl.IndexOf(',');
                if (commaIndex == -1) return;

                var base64 = base64DataUrl.Substring(commaIndex + 1);
                var header = base64DataUrl.Substring(0, commaIndex);
                
                string extension = "png";
                if (header.Contains("jpeg") || header.Contains("jpg")) extension = "jpg";

                var bytes = Convert.FromBase64String(base64);
                
                var tempFileName = $"Kivagas_{DateTime.Now:yyyyMMdd_HHmmss}.{extension}";
                var tempFilePath = Path.Combine(Microsoft.Maui.Storage.FileSystem.CacheDirectory, tempFileName);
                
                await File.WriteAllBytesAsync(tempFilePath, bytes);

                AppState.AttachedImages.Add(new AttachedImage
                {
                    FilePath = tempFilePath,
                    FileName = tempFileName,
                    Base64Thumbnail = base64DataUrl
                });

                AppState.StatusText = "Kép beillesztve a vágólapról.";
                StateHasChanged();
            }
            catch (Exception ex)
            {
                AppState.StatusText = $"Hiba a beillesztett kép feldolgozásakor: {ex.Message}";
                StateHasChanged();
            }
        }

        private async Task CopyImagesAsync()
        {
            if (!AppState.AttachedImages.Any()) return;
            var paths = AppState.AttachedImages.Select(x => x.FilePath).ToList();
            await ImageClipboardService.CopyImagesToClipboardAsync(paths);
            AppState.StatusText = $"{paths.Count} kép a vágólapra másolva (Ctrl+B).";
            StateHasChanged();
        }

        private async Task PickImagesAsync()
        {
            try
            {
                var results = await Microsoft.Maui.Storage.FilePicker.Default.PickMultipleAsync(new Microsoft.Maui.Storage.PickOptions
                {
                    PickerTitle = "Válassz képeket",
                    FileTypes = Microsoft.Maui.Storage.FilePickerFileType.Images
                });

                if (results != null)
                {
                    foreach (var result in results)
                    {
                        if (AppState.AttachedImages.Any(x => x.FilePath == result.FullPath)) continue;

                        using var stream = await result.OpenReadAsync();
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        var bytes = ms.ToArray();
                        var base64 = Convert.ToBase64String(bytes);
                        var ext = Path.GetExtension(result.FileName).TrimStart('.');
                        var mime = ext.ToLower() == "png" ? "image/png" : "image/jpeg";

                        AppState.AttachedImages.Add(new AttachedImage
                        {
                            FilePath = result.FullPath,
                            FileName = result.FileName,
                            Base64Thumbnail = $"data:{mime};base64,{base64}"
                        });
                    }
                    StateHasChanged();
                }
            }
            catch (Exception ex)
            {
                AppState.StatusText = $"Hiba a kép kiválasztásakor: {ex.Message}";
            }
        }

        [Inject]
        private ProjectSettingsService ProjectSettingsService { get; set; } = null!;
        [Inject]
        private IImageClipboardService ImageClipboardService { get; set; } = null!;

        [CascadingParameter]
        public LlmContextCollector.Components.Pages.Home? HomeRef { get; set; }

        private void ToggleTopDropdown() { _isTopDropdownOpen = !_isTopDropdownOpen; _isBottomDropdownOpen = false; _isDocsDropdownOpen = false; }
        private void ToggleBottomDropdown() { _isBottomDropdownOpen = !_isBottomDropdownOpen; _isTopDropdownOpen = false; _isDocsDropdownOpen = false; }
        private void ToggleDocsDropdown() { _isDocsDropdownOpen = !_isDocsDropdownOpen; _isTopDropdownOpen = false; _isBottomDropdownOpen = false; }
        private void SelectTopPrompt(Guid id) { AppState.ActiveGlobalPromptId = id; _isTopDropdownOpen = false; }
        private void SelectBottomPrompt(Guid id) { AppState.ActiveGlobalPromptId = id; _isBottomDropdownOpen = false; }

        public void CloseCustomDropdowns()
        {
            if (_isTopDropdownOpen || _isBottomDropdownOpen || _isDocsDropdownOpen)
            {
                _isTopDropdownOpen = false;
                _isBottomDropdownOpen = false;
                _isDocsDropdownOpen = false;
                StateHasChanged();
            }
        }

        private async Task SaveProjectSettingsAsync()
        {
            await ProjectSettingsService.SaveSettingsForProjectAsync(AppState.ProjectRoot);
        }

        private void ToggleDocumentSelection(AttachableDocument doc)
        {
            doc.IsSelected = !doc.IsSelected;
            _ = SaveProjectSettingsAsync();
            StateHasChanged();
        }

        private void EditDocument(AttachableDocument doc)
        {
            _isDocsDropdownOpen = false;
            HomeRef?.ShowAttachableDocDialog(doc);
        }

        private void AddNewDocument()
        {
            _isDocsDropdownOpen = false;
            HomeRef?.ShowAttachableDocDialog(null);
        }

        private async Task HandleAdoKeyup(KeyboardEventArgs e)
        {
            if (e.Key == "Enter" && _adoWorkItemIdToLoad.HasValue)
            {
                await LoadAdoWorkItemAsync();
            }
        }

        private async Task LoadAdoWorkItemAsync()
        {
            if (!_adoWorkItemIdToLoad.HasValue) return;

            AppState.ShowLoading($"ADO Work Item {_adoWorkItemIdToLoad} lekérése...");
            try
            {
                var result = await SettingsStore.GetFormattedWorkItemAsync(_adoWorkItemIdToLoad.Value);
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    // Szöveg betöltése
                    if (!string.IsNullOrWhiteSpace(AppState.PromptText))
                    {
                        AppState.PromptText = result.Text + "\n\n---\n\n" + AppState.PromptText;
                    }
                    else
                    {
                        AppState.PromptText = result.Text;
                    }

                    // Képek csatolása a UI szálon
                    foreach (var img in result.Images)
                    {
                        await InvokeAsync(() =>
                        {
                            // Most már az egyedi FilePath alapján nézzük az egyezőséget
                            if (!AppState.AttachedImages.Any(x => x.FilePath == img.FilePath))
                            {
                                AppState.AttachedImages.Add(img);
                                StateHasChanged();
                            }
                        });
                    }

                    AppState.StatusText = $"Work Item {_adoWorkItemIdToLoad} betöltve ({result.Images.Count} képpel).";
                    _adoWorkItemIdToLoad = null;
                }
            }
            catch (Exception ex)
            {
                AppState.StatusText = "Hiba az ADO elem betöltésekor.";
                await JSRuntime.InvokeVoidAsync("alert", $"Nem sikerült az ADO adatokat lekérni: {ex.Message}");
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        private async Task CopyTemplateById(Guid id)
        {
            var prompt = AppState.PromptTemplates.FirstOrDefault(p => p.Id == id);
            if (prompt != null)
            {
                await Clipboard.SetTextAsync(prompt.Content);
                AppState.StatusText = $"'{prompt.Title}' sablon tartalom másolva a vágólapra.";
            }
        }

        private async Task CopyToClipboard()
        {
            var sortedPaths = _sortedFiles.Select(f => f.RelativePath);
            
            var content = await ContextProcessingService.BuildContextForClipboardAsync(
                AppState.IncludePromptInCopy, 
                AppState.IncludeSystemPromptInCopy, 
                AppState.IncludeFilesInCopy,
                sortedPaths);

            if (string.IsNullOrWhiteSpace(content))
            {
                AppState.StatusText = "Nincs másolható tartalom (se fájl, se prompt).";
                return;
            }

            await OnHistorySaveRequested.InvokeAsync();
            await Clipboard.SetTextAsync(content);
            AppState.StatusText = $"Tartalom másolva ({content.Length} kar.). Előzmény mentve.";

            _copyButtonText = "Másolva! ✓";
            StateHasChanged();
            await Task.Delay(2000);
            _copyButtonText = "Másolás";
            StateHasChanged();
        }

        #region Browser Mode

        private async Task OpenAiStudioBrowser()
        {
            StateHasChanged();
            BrowserService.OpenBrowser("https://aistudio.google.com/");
        }

        private void CloseBrowserMode()
        {
            BrowserService.CloseBrowser();
            StateHasChanged();
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

        [Parameter] public EventCallback<DiffResultArgs> OnRequestLocalizationPath { get; set; }

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
                
                var finalArgs = new DiffResultArgs(
                    diffArgs.GlobalExplanation, 
                    diffArgs.DiffResults, 
                    diffArgs.FullLlmResponse, 
                    AppState.PromptText, 
                    diffArgs.LocalizationData);

                if (!string.IsNullOrEmpty(finalArgs.LocalizationData))
                {
                    var matches = Regex.Matches(diffArgs.LocalizationData, @"<data name=""([^""]+)""[^>]*>\s*<value>([\s\S]*?)<\/value>\s*<\/data>", RegexOptions.IgnoreCase);
                    foreach (Match m in matches)
                    {
                        diffArgs.DiffResults.Add(new DiffResult
                        {
                            Path = $"[LOC] {m.Groups[1].Value}",
                            NewContent = m.Groups[2].Value,
                            OldContent = "",
                            Status = DiffStatus.Modified,
                            Explanation = "Lokalizációs kulcs a válaszból.",
                            IsSelectedForAccept = true
                        });
                    }
                }

                if (!finalArgs.DiffResults.Any())
                {
                    await JSRuntime.InvokeVoidAsync("alert", "A válasz nem tartalmazott feldolgozható kódot, lokalizációt vagy kérdéseket sem.");
                }
                else
                {
                    AppState.StatusText = $"{finalArgs.DiffResults.Count} elem feldolgozva. Változások ablak megnyitva.";
                    await OnShowDiffDialog.InvokeAsync(finalArgs);
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
                var diffArgs = await GitWorkflowService.PrepareGitDiffForReviewAsync(AppState.PromptText);
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

        [Parameter]
        public EventCallback<LocalAiContextArgs> OnShowLocalAiChat { get; set; }

        private async Task ProcessWithLocalAiAsync()
        {
            try
            {
                var sortedPaths = _sortedFiles.Select(f => f.RelativePath);
                
                var prompt = AppState.PromptText;
                var system = await PromptService.GetSystemPromptAsync();
                var files = await ContextProcessingService.BuildContextForClipboardAsync(false, false, true, sortedPaths);

                await OnShowLocalAiChat.InvokeAsync(new LocalAiContextArgs(prompt, system, files));
            }
            catch (Exception ex)
            {
                await JSRuntime.InvokeVoidAsync("alert", $"Hiba a lokális AI hívása előkészítésekor: {ex.Message}");
                AppState.StatusText = "Hiba a lokális AI hívásakor.";
            }
        }

        public record LocalAiContextArgs(string Prompt, string System, string Files);

        #endregion

        #region List and Preview Search

        private async Task HandleContextSearchKeyup(KeyboardEventArgs e)
        {
            UpdateSortedFiles();
            SortFiles();
            await Task.CompletedTask;
        }

        private void ClearContextSearch()
        {
            _contextSearchTerm = string.Empty;
            UpdateSortedFiles();
            SortFiles();
        }

        public async Task SearchInPreview(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                await ClearPreviewSearch();
                return;
            }
            
            if (_showReferences)
            {
                _showReferences = false;
            }

            _previewSearchTerm = searchTerm;
            _isInitialPreviewSearch = true;
            UpdatePreviewMarkup();
            StateHasChanged();
            
            await Task.Delay(10); 
            await ScrollToCurrentPreviewMatch();
        }
        
        private async Task ToggleReferences(ChangeEventArgs e)
        {
            _showReferences = (bool)(e.Value ?? false);
            if (_showReferences)
            {
                _previewSearchTerm = string.Empty;
                await BuildProjectFileMap();
            }
            UpdatePreviewMarkup();
            await InvokeAsync(StateHasChanged);
        }

        private async Task BuildProjectFileMap()
        {
            _projectTypeMap.Clear();
            if (string.IsNullOrEmpty(AppState.ProjectRoot)) return;

            void ScanNodes(IEnumerable<FileNode> nodes)
            {
                foreach (var node in nodes)
                {
                    if (node.IsDirectory)
                    {
                        ScanNodes(node.Children);
                    }
                    else
                    {
                        var ext = Path.GetExtension(node.Name).ToLowerInvariant();
                        if (ext == ".cs" || ext == ".razor" || ext == ".cshtml")
                        {
                            var typeName = Path.GetFileNameWithoutExtension(node.Name);
                            if (!_projectTypeMap.ContainsKey(typeName))
                            {
                                var relPath = Path.GetRelativePath(AppState.ProjectRoot, node.FullPath).Replace('\\', '/');
                                _projectTypeMap[typeName] = relPath;
                            }
                        }
                    }
                }
            }
            
            await Task.Run(() => ScanNodes(AppState.FileTree));
        }

        [JSInvokable]
        public void OnReferenceClicked(string typeName)
        {
            if (_projectTypeMap.TryGetValue(typeName, out var relPath))
            {
                var filesToProcess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                filesToProcess.Add(relPath);

                var related = ReferenceFinderService.GetRelatedFilesByConvention(relPath, AppState.FileTree, AppState.ProjectRoot);
                foreach (var r in related)
                {
                    filesToProcess.Add(r);
                }

                bool isAdding = !AppState.SelectedFilesForContext.Contains(relPath);
                int changesCount = 0;

                foreach (var file in filesToProcess)
                {
                    if (!IsFileAvailable(file)) continue;

                    if (isAdding)
                    {
                        if (!AppState.SelectedFilesForContext.Contains(file))
                        {
                            AppState.SelectedFilesForContext.Add(file);
                            changesCount++;
                        }
                    }
                    else
                    {
                        if (AppState.SelectedFilesForContext.Contains(file))
                        {
                            AppState.SelectedFilesForContext.Remove(file);
                            changesCount++;
                        }
                    }
                }

                if (changesCount > 0)
                {
                    AppState.SaveContextListState();
                    AppState.StatusText = isAdding
                        ? $"{changesCount} fájl hozzáadva (ref)."
                        : $"{changesCount} fájl eltávolítva (ref).";
                }
            }
        }

        private bool IsFileAvailable(string relPath)
        {
            if (string.IsNullOrEmpty(AppState.ProjectRoot)) return false;
            try
            {
                var fullPath = Path.Combine(AppState.ProjectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                var node = AppState.FindNodeByPath(fullPath);
                return node != null && node.IsVisible;
            }
            catch
            {
                return false;
            }
        }

        private void UpdatePreviewMarkup()
        {
            if (string.IsNullOrEmpty(_previewContent))
            {
                _previewContentMarkup = new MarkupString("");
                ResetPreviewSearchState();
                return;
            }
            
            var encodedContent = HttpUtility.HtmlEncode(_previewContent);

            if (_showReferences)
            {
                var highlightedContent = Regex.Replace(encodedContent, @"\b[A-Z][a-zA-Z0-9_]*\b", m =>
                {
                    var token = m.Value;
                    if (_projectTypeMap.TryGetValue(token, out var relPath))
                    {
                        bool isInContext = AppState.SelectedFilesForContext.Contains(relPath);
                        string cssClass = isInContext ? "ref-badge ref-present" : "ref-badge ref-missing";
                        string title = isInContext ? "Kontextusban (Kattints az eltávolításhoz)" : "Nincs a kontextusban (Kattints a hozzáadáshoz)";
                        return $"<span class=\"{cssClass}\" data-type-name=\"{token}\" title=\"{title}\">{token}</span>";
                    }
                    return token;
                });
                
                _previewContentMarkup = new MarkupString(highlightedContent);
                ResetPreviewSearchState();
                return;
            }

            if (string.IsNullOrWhiteSpace(_previewSearchTerm))
            {
                _previewContentMarkup = new MarkupString(encodedContent);
                ResetPreviewSearchState();
                return;
            }

            var term = _previewSearchTerm;
            int matchCount = 0;
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

            var highlightedSearch = Regex.Replace(encodedContent,
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

            _previewContentMarkup = new MarkupString(highlightedSearch);
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
            _objRef?.Dispose();
            AppState.SelectedFilesForContext.CollectionChanged -= OnSelectedFilesChanged;
            AppState.PropertyChanged -= OnAppStateChanged;
            
            if (BrowserService != null)
            {
                BrowserService.OnContentExtracted -= HandleBrowserContentExtracted;
            }
        }
    }

    public class DiffResultArgs
    {
        public string GlobalExplanation { get; }
        public List<DiffResult> DiffResults { get; }
        public string FullLlmResponse { get; }
        public string LocalizationData { get; }
        public string OriginalPrompt { get; }

        public DiffResultArgs(string globalExplanation, List<DiffResult> diffResults, string fullLlmResponse, string originalPrompt = "", string localizationData = "")
        {
            GlobalExplanation = globalExplanation;
            DiffResults = diffResults;
            FullLlmResponse = fullLlmResponse;
            OriginalPrompt = originalPrompt;
            LocalizationData = localizationData;
        }
    }
}