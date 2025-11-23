using LlmContextCollector.AI;
using LlmContextCollector.AI.Embeddings;
using LlmContextCollector.Components.Pages.HomePanels;
using LlmContextCollector.Models;
using LlmContextCollector.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using System.ComponentModel;
using System.Text;

namespace LlmContextCollector.Components.Pages
{
    public partial class Home : ComponentBase, IDisposable
    {
        [Inject]
        private AppState AppState { get; set; } = null!;
        [Inject]
        private HistoryService HistoryService { get; set; } = null!;
        [Inject]
        private ReferenceFinderService ReferenceFinder { get; set; } = null!;
        [Inject]
        private IClipboard Clipboard { get; set; } = null!;
        [Inject]
        private EmbeddingIndexService EmbeddingIndexService { get; set; } = null!;
        [Inject]
        private GitService GitService { get; set; } = null!;
        [Inject]
        private AzureDevOpsService AzureDevOpsService { get; set; } = null!;
        [Inject]
        private SettingsService SettingsService { get; set; } = null!;
        [Inject]
        private IJSRuntime JSRuntime { get; set; } = null!;
        [Inject]
        private GitWorkflowService GitWorkflowService { get; set; } = null!;
        [Inject]
        private ProjectService ProjectService { get; set; } = null!;
        [Inject]
        private HistoryManagerService HistoryManagerService { get; set; } = null!;
        [Inject]
        private FileContextService FileContextService { get; set; } = null!;
        [Inject]
        private IEmbeddingProvider EmbeddingProvider { get; set; } = null!;
        [Inject]
        private ProjectSettingsService ProjectSettingsService { get; set; } = null!;


        private ContextPanel? _contextPanelRef;

        private List<string> _selectedInContextList = new();
        private FileNode? _lastInteractionNode;

        private bool isPromptManagerVisible = false;

        private bool _isSettingsDialogVisible = false;
        private bool _isAzureDevOpsDialogVisible = false;
        private bool _isDocumentSearchDialogVisible = false;

        private bool _showTreeContextMenu = false;
        private bool _showListContextMenu = false;
        private double _contextMenuX;
        private double _contextMenuY;

        protected override async Task OnInitializedAsync()
        {
            AppState.PropertyChanged += OnAppStateChanged;
            await HistoryService.LoadHistoryAsync();
            await AppState.LoadPromptsAsync();
            await LoadSettingsAsync();
            await ApplyTheme();

            var latest = AppState.HistoryEntries.FirstOrDefault();
            if (latest != null)
            {
                await LoadHistoryEntry(latest);
            }
            else
            {
                AppState.StatusText = "Készen áll. Nincs betölthető előzmény.";
            }
        }

        private async void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppState.SelectedPromptTemplateId))
            {
                AppState.UpdatePromptTextFromTemplate();
            }
            await InvokeAsync(StateHasChanged);
        }

        private async Task ApplyFiltersAndReload(bool preserveSelection = true, bool showIndicator = true)
        {
            if (showIndicator)
            {
                AppState.ShowLoading("Projektfájlok keresése és szűrése...");
                await Task.Delay(1);
            }
            else
            {
                AppState.StatusText = "Projektfájlok keresése és szűrése...";
            }

            try
            {
                // Beállítások mentése az újratöltés előtt
                if (!string.IsNullOrEmpty(AppState.ProjectRoot))
                {
                    await ProjectSettingsService.SaveSettingsForProjectAsync(AppState.ProjectRoot);
                }

                await ProjectService.ReloadProjectAsync(preserveSelection);
            }
            finally
            {
                if (showIndicator)
                {
                    AppState.HideLoading();
                }
            }
        }


        private void GetAllFileNodes(IEnumerable<FileNode> nodes, List<FileNode> fileNodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory)
                {
                    GetAllFileNodes(node.Children, fileNodes);
                }
                else
                {
                    fileNodes.Add(node);
                }
            }
        }

        private async Task HandleNodeClick((FileNode Node, MouseEventArgs Args) payload)
        {
            var node = payload.Node;
            var e = payload.Args;

            if (e != null)
            {
                // Range Selection: Shift OR (Ctrl + Alt)
                bool isRangeSelect = e.ShiftKey || (e.CtrlKey && e.AltKey);
                bool isMultiSelect = e.CtrlKey && !e.AltKey && !e.ShiftKey;

                if (isRangeSelect && _lastInteractionNode != null)
                {
                    var visibleNodes = new List<FileNode>();
                    GetVisibleNodesLinear(AppState.FileTree, visibleNodes);

                    var start = visibleNodes.IndexOf(_lastInteractionNode);
                    var end = visibleNodes.IndexOf(node);

                    if (start != -1 && end != -1)
                    {
                        var low = Math.Min(start, end);
                        var high = Math.Max(start, end);

                        // Ha nincs lenyomva a Ctrl (csak Shift), akkor töröljük a többit.
                        // Ha Ctrl is le van nyomva (pl Ctrl+Alt), akkor hozzáadunk a meglévőhöz.
                        if (!e.CtrlKey)
                        {
                            DeselectAllNodes(AppState.FileTree);
                        }

                        for (int i = low; i <= high; i++)
                        {
                            visibleNodes[i].IsSelectedInTree = true;
                        }
                    }
                }
                else if (isMultiSelect)
                {
                    node.IsSelectedInTree = !node.IsSelectedInTree;
                    _lastInteractionNode = node;
                }
                else
                {
                    DeselectAllNodes(AppState.FileTree);
                    node.IsSelectedInTree = true;
                    _lastInteractionNode = node;
                }
            }
            else
            {
                // Programmatic selection (e.g. search navigation)
                DeselectAllNodes(AppState.FileTree);
                node.IsSelectedInTree = true;
                _lastInteractionNode = node;
                AppState.ExpandNodeParents(node);

                await Task.Delay(10);

                var elementId = "filenode-" + Convert.ToBase64String(Encoding.UTF8.GetBytes(node.FullPath))
                    .Replace("=", "")
                    .Replace("+", "-")
                    .Replace("/", "_");

                await JSRuntime.InvokeVoidAsync("scrollToElement", elementId);
            }

            var selectedNodes = new List<FileNode>();
            FindSelectedNodes(AppState.FileTree, selectedNodes);

            var selectedPathsFromTree = selectedNodes
                .Where(n => !n.IsDirectory)
                .Select(n => Path.GetRelativePath(AppState.ProjectRoot, n.FullPath).Replace('\\', '/'))
                .ToList();

            _selectedInContextList = selectedPathsFromTree
                .Intersect(AppState.SelectedFilesForContext)
                .ToList();

            if (_contextPanelRef != null)
            {
                if (selectedNodes.Count == 1 && !selectedNodes[0].IsDirectory)
                {
                    var relativePath = Path.GetRelativePath(AppState.ProjectRoot, selectedNodes[0].FullPath).Replace('\\', '/');
                    await _contextPanelRef.UpdatePreview(relativePath);

                    if (e == null && AppState.SearchInContent && !string.IsNullOrWhiteSpace(AppState.SearchTerm))
                    {
                        await _contextPanelRef.SearchInPreview(AppState.SearchTerm);
                    }
                }
                else
                {
                    await _contextPanelRef.UpdatePreview(null);
                }
            }

            await InvokeAsync(StateHasChanged);
        }

        private void GetVisibleNodesLinear(IEnumerable<FileNode> nodes, List<FileNode> list)
        {
            foreach (var node in nodes)
            {
                if (!node.IsVisible) continue;

                list.Add(node);

                if (node.IsDirectory && node.IsExpanded)
                {
                    GetVisibleNodesLinear(node.Children, list);
                }
            }
        }


        private async Task HandleContextSelectionChanged(List<string> selectedFiles)
        {
            _selectedInContextList = selectedFiles;
            DeselectAllNodes(AppState.FileTree);

            if (_selectedInContextList.Any() && !string.IsNullOrEmpty(AppState.ProjectRoot))
            {
                FileNode? lastFoundNode = null;
                foreach (var relPath in _selectedInContextList)
                {
                    if (relPath.StartsWith("[ADO]")) continue;
                    var fullPath = Path.Combine(AppState.ProjectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                    var node = AppState.FindNodeByPath(fullPath);
                    if (node != null)
                    {
                        node.IsSelectedInTree = true;
                        lastFoundNode = node;
                    }
                }

                if (lastFoundNode != null)
                {
                    AppState.ExpandNodeParents(lastFoundNode);
                }
            }

            if (_contextPanelRef != null)
            {
                var pathToPreview = selectedFiles.Count == 1 ? selectedFiles.First() : null;
                await _contextPanelRef.UpdatePreview(pathToPreview);
            }

            if (_selectedInContextList.Count == 1)
            {
                var relPath = _selectedInContextList.First();
                if (relPath.StartsWith("[ADO]"))
                {
                    AppState.StatusText = $"Dokumentum kiválasztva: {relPath.Substring(5)}";
                }
                else
                {
                    var fullPath = Path.Combine(AppState.ProjectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                    var node = AppState.FindNodeByPath(fullPath);
                    if (node != null)
                    {
                        AppState.StatusText = $"Fájl kiválasztva: {relPath}";
                    }
                    else
                    {
                        AppState.StatusText = $"Fájl nem látható (szűrés aktív?): {relPath}";
                    }
                }
            }

            await InvokeAsync(StateHasChanged);
        }

        protected async Task AddSelectedFilesToContext()
        {
            HideContextMenus();
            await FileContextService.AddSelectedTreeNodesToContextAsync();
        }

        protected void RemoveSelectedFilesFromContext()
        {
            HideContextMenus();
            FileContextService.RemoveFileListSelectionFromContext(_selectedInContextList);
            _selectedInContextList.Clear();
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

        private void DeselectAllNodes(IEnumerable<FileNode> nodes)
        {
            foreach (var node in nodes)
            {
                node.IsSelectedInTree = false;
                if (node.Children.Any())
                {
                    DeselectAllNodes(node.Children);
                }
            }
        }

        #region Context Menus & Exclude

        private void ShowTreeContextMenu(MouseEventArgs e)
        {
            var selectedNodes = new List<FileNode>();
            FindSelectedNodes(AppState.FileTree, selectedNodes);
            if (selectedNodes.Any())
            {
                _showTreeContextMenu = true;
                _showListContextMenu = false;
                _contextMenuX = e.ClientX;
                _contextMenuY = e.ClientY;
                StateHasChanged();
            }
        }

        private void ShowListContextMenu(MouseEventArgs e)
        {
            if (_selectedInContextList.Any())
            {
                _showListContextMenu = true;
                _showTreeContextMenu = false;
                _contextMenuX = e.ClientX;
                _contextMenuY = e.ClientY;
                StateHasChanged();
            }
        }

        private void HideContextMenus()
        {
            if (_showTreeContextMenu || _showListContextMenu)
            {
                _showTreeContextMenu = false;
                _showListContextMenu = false;
                StateHasChanged();
            }
        }

        private async Task ExcludeSelectedFromTree()
        {
            HideContextMenus();
            var selectedNodes = new List<FileNode>();
            FindSelectedNodes(AppState.FileTree, selectedNodes);

            if (!selectedNodes.Any()) return;

            var newIgnores = new List<string>();
            foreach (var node in selectedNodes)
            {
                var relativePath = Path.GetRelativePath(AppState.ProjectRoot, node.FullPath).Replace('\\', '/');
                newIgnores.Add(relativePath);
                node.IsSelectedInTree = false;
            }

            var existingIgnores = AppState.IgnorePatternsRaw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var newUniqueIgnores = newIgnores.Except(existingIgnores).ToList();
            if (newUniqueIgnores.Any())
            {
                existingIgnores.AddRange(newUniqueIgnores);
                AppState.IgnorePatternsRaw = string.Join('\n', existingIgnores.Distinct());
                AppState.StatusText = $"{newUniqueIgnores.Count} új kizárás hozzáadva. Újratöltés...";
                await ApplyFiltersAndReload(true);
            }
            else
            {
                AppState.StatusText = "A kijelölt elemek már a kizárási listán vannak.";
            }
        }

        private async Task CopySelectedNodeName()
        {
            HideContextMenus();
            var selectedNodes = new List<FileNode>();
            FindSelectedNodes(AppState.FileTree, selectedNodes);

            if (selectedNodes.Count == 1)
            {
                await Clipboard.SetTextAsync(selectedNodes.First().Name);
                AppState.StatusText = $"Név másolva: {selectedNodes.First().Name}";
            }
            else
            {
                AppState.StatusText = "A név másolásához pontosan egy elemet kell kiválasztani.";
            }
        }

        private async Task CopySelectedNodePath()
        {
            HideContextMenus();
            var selectedNodes = new List<FileNode>();
            FindSelectedNodes(AppState.FileTree, selectedNodes);

            if (selectedNodes.Count == 1)
            {
                var node = selectedNodes.First();
                var relativePath = Path.GetRelativePath(AppState.ProjectRoot, node.FullPath).Replace('\\', '/');
                await Clipboard.SetTextAsync(relativePath);
                AppState.StatusText = $"Útvonal másolva: {relativePath}";
            }
            else
            {
                AppState.StatusText = "Az elérési út másolásához pontosan egy elemet kell kiválasztani.";
            }
        }

        #endregion

        #region History

        private async Task LoadHistoryEntry(HistoryEntry entry)
        {
            await HistoryManagerService.ApplyHistoryEntryAsync(entry);
        }

        #endregion

        #region Dialog Handling
        private async Task ApplyTheme()
        {
            await JSRuntime.InvokeVoidAsync("setTheme", AppState.Theme);
        }

        private async Task LoadSettingsAsync()
        {
            var settings = await SettingsService.GetSettingsAsync();
            AppState.Theme = settings.Theme;
            AppState.GroqApiKey = settings.GroqApiKey;
            AppState.GroqModel = settings.GroqModel;
            AppState.GroqMaxOutputTokens = settings.GroqMaxOutputTokens;
            AppState.GroqApiUrl = settings.GroqApiUrl;
            AppState.OpenRouterApiKey = settings.OpenRouterApiKey;
            AppState.OpenRouterModel = settings.OpenRouterModel;
            AppState.OpenRouterSiteUrl = settings.OpenRouterSiteUrl;
            AppState.OpenRouterSiteName = settings.OpenRouterSiteName;
        }

        private async Task OnPromptManagerClose()
        {
            isPromptManagerVisible = false;
            await AppState.LoadPromptsAsync();
            StateHasChanged();
        }

        private void ShowSettingsDialog()
        {
            _isSettingsDialogVisible = true;
            StateHasChanged();
        }

        private async Task OnSettingsDialogClose()
        {
            _isSettingsDialogVisible = false;
            await ApplyTheme();
            StateHasChanged();
        }

        private void ShowDocumentSearchDialog()
        {
            _isDocumentSearchDialogVisible = true;
            StateHasChanged();
        }

        private void OnDocumentSearchDialogClose()
        {
            _isDocumentSearchDialogVisible = false;
            StateHasChanged();
        }

        private void OnAzureDevOpsDialogClose()
        {
            _isAzureDevOpsDialogVisible = false;
            StateHasChanged();
        }

        private async Task HandleDownloadWorkItemsAsync(bool isIncremental)
        {
            _isAzureDevOpsDialogVisible = false;
            if (string.IsNullOrWhiteSpace(AppState.ProjectRoot))
            {
                await JSRuntime.InvokeVoidAsync("alert", "A work item-ek letöltése előtt válasszon ki egy projekt mappát.");
                return;
            }

            AppState.ShowLoading("Azure DevOps work item-ek letöltése...");
            try
            {
                await AzureDevOpsService.SaveSettingsForCurrentProjectAsync();

                await AzureDevOpsService.DownloadWorkItemsAsync(
                    AppState.AzureDevOpsOrganizationUrl,
                    AppState.AzureDevOpsProject,
                    AppState.AzureDevOpsPat,
                    AppState.AzureDevOpsRepository,
                    AppState.AzureDevOpsIterationPath,
                    AppState.ProjectRoot,
                    isIncremental);

                await AzureDevOpsService.SaveSettingsForCurrentProjectAsync(DateTime.UtcNow);
                AzureDevOpsService.UpdateAdoPaths(AppState.ProjectRoot);

                AppState.StatusText = "Azure DevOps work item-ek sikeresen letöltve.";
            }
            catch (Exception ex)
            {
                AppState.StatusText = $"Hiba a work item-ek letöltése közben.";
                await JSRuntime.InvokeVoidAsync("alert", $"Hiba a work item-ek letöltése közben: {ex.Message}");
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        private void AddRelevantFiles(List<string> filesToAdd)
        {
            var currentFiles = AppState.SelectedFilesForContext.ToHashSet();
            var initialCount = currentFiles.Count;
            currentFiles.UnionWith(filesToAdd);
            var addedCount = currentFiles.Count - initialCount;

            AppState.SelectedFilesForContext.Clear();
            foreach (var file in currentFiles.OrderBy(f => f))
            {
                AppState.SelectedFilesForContext.Add(file);
            }
            AppState.SaveContextListState();

            AppState.StatusText = $"{addedCount} új releváns fájl hozzáadva a listához.";
            OnDocumentSearchDialogClose();
        }

        private void ShowDiffDialog(DiffResultArgs args)
        {
            AppState.DiffGlobalExplanation = args.GlobalExplanation;
            AppState.DiffResults = args.DiffResults;
            AppState.DiffFullLlmResponse = args.FullLlmResponse;
            AppState.IsDiffDialogVisible = true;
            StateHasChanged();
        }

        private async Task OnDiffDialogClose()
        {
            AppState.IsDiffDialogVisible = false;
            await Task.CompletedTask;
        }

        private async Task HandleAcceptChanges(List<DiffResult> acceptedResults)
        {
            var (acceptedCount, errorCount) = await GitWorkflowService.AcceptChangesAsync(acceptedResults);
            AppState.StatusText = $"Változások elfogadása befejezve. Elfogadva: {acceptedCount}, Hiba: {errorCount}.";
            StateHasChanged();
        }

        private async Task HandleCreateBranchAsync(string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                await JSRuntime.InvokeVoidAsync("alert", "A branch név nem lehet üres.");
                return;
            }

            AppState.ShowLoading($"'{branchName}' branch létrehozása...");
            try
            {
                await GitWorkflowService.CreateAndCheckoutBranchAsync(branchName);
            }
            catch (Exception ex)
            {
                AppState.StatusText = "Hiba a branch létrehozásakor.";
                await JSRuntime.InvokeVoidAsync("alert", $"Hiba a branch létrehozásakor:\n\n{ex.Message}");
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        private async Task HandleCommitAsync(CommitAndPushArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.CommitMessage) || !args.AcceptedFiles.Any())
            {
                AppState.StatusText = "Hiba: Hiányzó commit üzenet vagy nincsenek elfogadandó fájlok.";
                return;
            }

            string loadingMessage = $"Változások commitolása a(z) '{args.BranchName}' branch-re...";
            AppState.ShowLoading(loadingMessage);
            try
            {
                await GitWorkflowService.CommitChangesAsync(args);
            }
            catch (Exception ex)
            {
                AppState.StatusText = $"Git hiba: {ex.Message}";
                await JSRuntime.InvokeVoidAsync("alert", $"Git commit sikertelen:\n\n{ex.Message}");
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        private async Task HandlePushAsync(CommitAndPushArgs args)
        {
            if (AppState.CurrentGitBranch != args.BranchName)
            {
                await JSRuntime.InvokeVoidAsync("alert", $"Nem a megfelelő branch-en vagy ('{AppState.CurrentGitBranch}'). Válts a '{args.BranchName}' branch-re a push előtt.");
                return;
            }

            AppState.ShowLoading($"Változások pusholása a(z) '{args.BranchName}' branch-re...");
            try
            {
                await GitWorkflowService.PushChangesAsync(args.BranchName);
                await OnDiffDialogClose();
            }
            catch (Exception ex)
            {
                AppState.StatusText = $"Git hiba: {ex.Message}";
                await JSRuntime.InvokeVoidAsync("alert", $"Git push sikertelen:\n\n{ex.Message}");
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        #endregion

        private async Task StartManualIndexing()
        {
            if (EmbeddingProvider is NullEmbeddingProvider)
            {
                await JSRuntime.InvokeVoidAsync("alert", "Az AI modell nem található vagy nem sikerült betölteni. Az indexelési funkció nem érhető el.");
                return;
            }
            var allFileNodes = new List<FileNode>();
            GetAllFileNodes(AppState.FileTree, allFileNodes);
            EmbeddingIndexService.StartBuildingIndex(allFileNodes);
        }

        private async Task StartIndexingAdo()
        {
            if (EmbeddingProvider is NullEmbeddingProvider)
            {
                await JSRuntime.InvokeVoidAsync("alert", "Az AI modell nem található vagy nem sikerült betölteni. Az indexelési funkció nem érhető el.");
                return;
            }
            EmbeddingIndexService.StartBuildingAdoIndex();
        }

        #region Panel Resizing
        private string _activeSplitter = "None";
        private bool _isResizing = false;
        private double _startX, _startY;
        private double _startLeftFlex, _startMiddleFlex, _startRightFlex;
        private double _startTopFlex, _startMidFlex, _startBotFlex;

        private void StartResize(MouseEventArgs e, string splitter)
        {
            _isResizing = true;
            _activeSplitter = splitter;
            _startX = e.ClientX;
            _startY = e.ClientY;

            _startLeftFlex = AppState.LeftPanelFlex;
            _startMiddleFlex = AppState.MiddlePanelFlex;
            _startRightFlex = AppState.RightPanelFlex;

            _startTopFlex = AppState.RightTopPanelFlex;
            _startMidFlex = AppState.RightMiddlePanelFlex;
            _startBotFlex = AppState.RightBottomPanelFlex;
        }

        private void StopResize()
        {
            _isResizing = false;
            _activeSplitter = "None";
        }

        private void OnMouseMove(MouseEventArgs e)
        {
            if (!_isResizing) return;

            var deltaX = e.ClientX - _startX;
            var deltaY = e.ClientY - _startY;

            const double flexFactor = 0.2;

            switch (_activeSplitter)
            {
                case "LeftMiddle":
                    var flexDeltaX1 = deltaX * flexFactor;
                    AppState.LeftPanelFlex = Math.Max(10, _startLeftFlex + flexDeltaX1);
                    AppState.MiddlePanelFlex = Math.Max(10, _startMiddleFlex - flexDeltaX1);
                    break;

                case "MiddleRight":
                    var flexDeltaX2 = deltaX * flexFactor;
                    AppState.MiddlePanelFlex = Math.Max(10, _startMiddleFlex + flexDeltaX2);
                    AppState.RightPanelFlex = Math.Max(10, _startRightFlex - flexDeltaX2);
                    break;

                case "RightTop":
                    var flexDeltaY1 = deltaY * flexFactor;
                    AppState.RightTopPanelFlex = Math.Max(10, _startTopFlex + flexDeltaY1);
                    AppState.RightMiddlePanelFlex = Math.Max(10, _startMidFlex - flexDeltaY1);
                    break;

                case "RightMiddle":
                    var flexDeltaY2 = deltaY * flexFactor;
                    AppState.RightMiddlePanelFlex = Math.Max(10, _startMidFlex + flexDeltaY2);
                    AppState.RightBottomPanelFlex = Math.Max(10, _startBotFlex - flexDeltaY2);
                    break;
            }
        }
        #endregion

        public void Dispose()
        {
            AppState.PropertyChanged -= OnAppStateChanged;
            EmbeddingIndexService.CancelIndexing();
        }
    }
}