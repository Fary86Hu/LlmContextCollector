using LlmContextCollector.Components.Pages.HomePanels;
using LlmContextCollector.Models;
using LlmContextCollector.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using System.ComponentModel;

namespace LlmContextCollector.Components.Pages
{
    public partial class Home : ComponentBase, IDisposable
    {
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

        private ContextPanel? _contextPanelRef;
        private List<string> _selectedInContextList = new();
        private List<RelevanceResult> _relevanceResults = new();

        private bool isPromptManagerVisible = false;
        private bool _isSettingsDialogVisible = false;
        private bool _isRelevanceDialogVisible = false;
        private bool _isAzureDevOpsDialogVisible = false;

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
            if (string.IsNullOrWhiteSpace(AppState.ProjectRoot) || !Directory.Exists(AppState.ProjectRoot))
            {
                AppState.StatusText = "Érvénytelen vagy nem létező mappa.";
                return;
            }

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
                EmbeddingIndexService.CancelIndexing();
                var filesToPreserve = preserveSelection ? AppState.SelectedFilesForContext.ToList() : new List<string>();
                AppState.SelectedFilesForContext.Clear();
                AppState.ResetContextListHistory();
                EmbeddingIndexService.ClearIndex();

                var tree = await FileService.ScanDirectoryAsync(AppState.ProjectRoot);
                AppState.SetFileTree(tree);

                var gitDir = Path.Combine(AppState.ProjectRoot, ".git");
                AppState.IsGitRepository = Directory.Exists(gitDir);
                if (AppState.IsGitRepository)
                {
                    var (branchName, success, _) = await GitService.GetCurrentBranchAsync();
                    if (success) AppState.CurrentGitBranch = branchName;
                }
                else
                {
                    AppState.CurrentGitBranch = string.Empty;
                }
                
                AppState.UpdateAdoPaths(AppState.ProjectRoot);

                var allFileNodes = new List<FileNode>();
                GetAllFileNodes(AppState.FileTree, allFileNodes);


                var allFilePaths = new HashSet<string>();
                GetAllFilePaths(AppState.FileTree, allFilePaths, AppState.ProjectRoot);

                AppState.SelectedFilesForContext.Clear();
                foreach (var file in filesToPreserve)
                {
                    // ADO fájlokat mindig megőrzünk
                    if (file.StartsWith("[ADO]") || allFilePaths.Contains(file))
                    {
                        AppState.SelectedFilesForContext.Add(file);
                    }
                }
                AppState.SaveContextListState();
                AppState.StatusText = $"Szkennelés befejezve. {allFilePaths.Count} fájl található a fa nézetben.";
            }
            finally
            {
                if (showIndicator)
                {
                    AppState.HideLoading();
                }
            }
        }

        private void GetAllFilePaths(IEnumerable<FileNode> nodes, HashSet<string> paths, string root)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory)
                {
                    GetAllFilePaths(node.Children, paths, root);
                }
                else
                {
                    paths.Add(Path.GetRelativePath(root, node.FullPath).Replace('\\', '/'));
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

            // Step 1: Update tree selection state (logic moved from FileTreePanel)
            if (!e.CtrlKey)
            {
                DeselectAllNodes(AppState.FileTree);
            }
            node.IsSelectedInTree = !node.IsSelectedInTree;

            // Step 2: Synchronize this new tree selection to the list
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
                // Step 3: Update preview panel based on the new selection state
                if (selectedNodes.Count == 1 && !selectedNodes[0].IsDirectory)
                {
                    var relativePath = Path.GetRelativePath(AppState.ProjectRoot, selectedNodes[0].FullPath).Replace('\\', '/');
                    await _contextPanelRef.UpdatePreview(relativePath);
                }
                else
                {
                    await _contextPanelRef.UpdatePreview(null);
                }
            }

            await InvokeAsync(StateHasChanged);
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
            var selectedNodes = new List<FileNode>();
            FindSelectedNodes(AppState.FileTree, selectedNodes);

            if (!selectedNodes.Any())
            {
                AppState.StatusText = "Nincs elem kiválasztva a fában a hozzáadáshoz.";
                return;
            }

            bool showLoading = AppState.ReferenceSearchDepth > 0;

            try
            {
                if (showLoading)
                {
                    AppState.ShowLoading("Fájlok hozzáadása és referenciák keresése...");
                    await Task.Delay(1);
                }

                var projectRootPath = AppState.ProjectRoot ?? string.Empty;
                var filesFromSelection = new HashSet<string>();

                foreach (var node in selectedNodes)
                {
                    AddNodeAndChildrenToSet(node, projectRootPath, filesFromSelection);
                    node.IsSelectedInTree = false;
                }

                var currentFiles = AppState.SelectedFilesForContext.ToHashSet();
                var initialCount = currentFiles.Count;
                currentFiles.UnionWith(filesFromSelection);

                if (AppState.ReferenceSearchDepth > 0 && filesFromSelection.Any())
                {
                    var foundRefs = await ReferenceFinder.FindReferencesAsync(filesFromSelection.ToList(), AppState.FileTree, projectRootPath, AppState.ReferenceSearchDepth);
                    var newRefsCount = foundRefs.Count(r => !currentFiles.Contains(r));
                    if (newRefsCount > 0) AppState.StatusText = $"{newRefsCount} új kapcsolódó fájl hozzáadva referenciák alapján.";
                    currentFiles.UnionWith(foundRefs);
                }

                var addedCount = currentFiles.Count - initialCount;
                if (addedCount > 0)
                {
                    AppState.SelectedFilesForContext.Clear();
                    foreach (var file in currentFiles.OrderBy(f => f))
                    {
                        AppState.SelectedFilesForContext.Add(file);
                    }
                    AppState.SaveContextListState();
                    if (!AppState.StatusText.Contains("referenciák"))
                    {
                        AppState.StatusText = $"{addedCount} fájl hozzáadva a kontextushoz.";
                    }
                }
                else
                {
                    AppState.StatusText = "Nem lett új fájl hozzáadva (már a listán voltak).";
                }
            }
            finally
            {
                if (showLoading)
                {
                    AppState.HideLoading();
                }
            }
        }

        private void AddNodeAndChildrenToSet(FileNode node, string root, HashSet<string> files)
        {
            if (node.IsDirectory && node.IsVisible)
            {
                foreach (var child in node.Children)
                {
                    AddNodeAndChildrenToSet(child, root, files);
                }
            }
            else if (node.IsVisible && !node.IsDirectory)
            {
                var relativePath = Path.GetRelativePath(root, node.FullPath).Replace('\\', '/');
                files.Add(relativePath);
            }
        }

        protected void RemoveSelectedFilesFromContext()
        {
            HideContextMenus();
            if (!_selectedInContextList.Any())
            {
                AppState.StatusText = "Nincs fájl kijelölve az eltávolításhoz.";
                return;
            }

            var removedCount = 0;
            foreach (var selectedFile in _selectedInContextList)
            {
                if (AppState.SelectedFilesForContext.Remove(selectedFile))
                {
                    removedCount++;
                }
            }
            _selectedInContextList.Clear();

            if (removedCount > 0)
            {
                AppState.SaveContextListState();
                AppState.StatusText = $"{removedCount} fájl eltávolítva a kontextusból.";
            }
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
            AppState.ShowLoading($"Előzmény betöltése: {Path.GetFileName(entry.RootFolder)}...");
            await Task.Delay(1);
            try
            {
                AppState.ProjectRoot = entry.RootFolder;
                AppState.IgnorePatternsRaw = entry.IgnoreFilter;

                var extensions = entry.ExtensionsFilter.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).ToHashSet();

                foreach (var ext in extensions)
                {
                    AppState.AddExtensionFilter(ext);
                }

                var currentExts = AppState.ExtensionFilters.Keys.ToList();
                var extensionStateChanged = false;
                foreach (var key in currentExts)
                {
                    var shouldBeEnabled = extensions.Contains(key);
                    if (AppState.ExtensionFilters[key] != shouldBeEnabled)
                    {
                        AppState.ExtensionFilters[key] = shouldBeEnabled;
                        extensionStateChanged = true;
                    }
                }
                if (extensionStateChanged)
                {
                    AppState.NotifyStateChanged(nameof(AppState.ExtensionFilters));
                }

                await ApplyFiltersAndReload(preserveSelection: false, showIndicator: false);

                AppState.SelectedFilesForContext.Clear();
                foreach (var file in entry.SelectedFiles)
                {
                    AppState.SelectedFilesForContext.Add(file);
                }
                AppState.ResetContextListHistory();
                AppState.SaveContextListState();

                AppState.PromptText = entry.PromptText;
                var matchingTemplate = AppState.PromptTemplates.FirstOrDefault(p => p.Title == entry.SelectedTemplateTitle);
                AppState.SelectedPromptTemplateId = matchingTemplate?.Id ?? Guid.Empty;
                AppState.StatusText = $"Előzmény betöltve: {Path.GetFileName(entry.RootFolder)}";
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        #endregion

        #region Dialog Handling

        private async Task LoadSettingsAsync()
        {
            var settings = await SettingsService.GetSettingsAsync();
            AppState.GroqApiKey = settings.GroqApiKey;
            AppState.GroqModel = settings.GroqModel;
            AppState.GroqMaxOutputTokens = settings.GroqMaxOutputTokens;
            AppState.GroqApiUrl = settings.GroqApiUrl;
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

        private void OnSettingsDialogClose()
        {
            _isSettingsDialogVisible = false;
            StateHasChanged();
        }

        private void ShowRelevanceDialog(RelevanceResultArgs args)
        {
            _relevanceResults = args.Results;
            _isRelevanceDialogVisible = true;
            StateHasChanged();
        }

        private async Task OnRelevanceDialogClose()
        {
            _isRelevanceDialogVisible = false;
            _relevanceResults.Clear();
            await InvokeAsync(StateHasChanged);
        }

        private void OnAzureDevOpsDialogClose()
        {
            _isAzureDevOpsDialogVisible = false;
            StateHasChanged();
        }

        private async Task HandleDownloadWorkItemsAsync()
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
                await AzureDevOpsService.DownloadWorkItemsAsync(
                    AppState.AzureDevOpsOrganizationUrl,
                    AppState.AzureDevOpsProject,
                    AppState.AzureDevOpsPat,
                    AppState.AzureDevOpsRepository,
                    AppState.AzureDevOpsIterationPath,
                    AppState.ProjectRoot);
                    
                AppState.UpdateAdoPaths(AppState.ProjectRoot);

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
            OnRelevanceDialogClose();
        }

        private void ShowDiffDialog(DiffResultArgs args)
        {
            AppState.DiffGlobalExplanation = args.GlobalExplanation;
            AppState.DiffResults = args.DiffResults;
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
            int acceptedCount = 0;
            int errorCount = 0;
            foreach (var result in acceptedResults)
            {
                try
                {
                    var fullPath = Path.Combine(AppState.ProjectRoot, result.Path.Replace('/', Path.DirectorySeparatorChar));

                    if (result.Status == DiffStatus.Deleted)
                    {
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                        }
                    }
                    else
                    {
                        var dir = Path.GetDirectoryName(fullPath);
                        if (dir != null && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        await File.WriteAllTextAsync(fullPath, result.NewContent);
                    }
                    result.Status = DiffStatus.Accepted;
                    acceptedCount++;
                }
                catch
                {
                    result.Status = DiffStatus.Error;
                    errorCount++;
                }
            }
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
                await GitService.CreateAndCheckoutBranchAsync(branchName);
                AppState.CurrentGitBranch = branchName;
                AppState.StatusText = $"Átváltva a(z) '{branchName}' branch-re.";
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

            string commitBranch = AppState.CurrentGitBranch;
            string loadingMessage;

            if (AppState.CurrentGitBranch != args.BranchName)
            {
                loadingMessage = $"Nincs új branch létrehozva. Commit a(z) '{commitBranch}' branch-re...";
            }
            else
            {
                loadingMessage = $"Változások commitolása a(z) '{commitBranch}' branch-re...";
            }

            AppState.ShowLoading(loadingMessage);
            try
            {
                await HandleAcceptChanges(args.AcceptedFiles);
                AppState.StatusText = "Fájlok mentve. Fájlok stage-elése...";
                await Task.Delay(1);

                var filePathsToStage = args.AcceptedFiles.Select(f => f.Path);
                await GitService.StageFilesAsync(filePathsToStage);
                AppState.StatusText = "Fájlok stage-elve. Commit létrehozása...";
                await Task.Delay(1);

                await GitService.CommitAsync(args.CommitMessage);
                AppState.StatusText = "Commit sikeres. A változások push-olhatók.";
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
                await GitService.PushAsync(args.BranchName);
                AppState.StatusText = $"Sikeres push a(z) '{args.BranchName}' branch-re!";
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

        private void StartManualIndexing()
        {
            var allFileNodes = new List<FileNode>();
            GetAllFileNodes(AppState.FileTree, allFileNodes);
            EmbeddingIndexService.StartBuildingIndex(allFileNodes);
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