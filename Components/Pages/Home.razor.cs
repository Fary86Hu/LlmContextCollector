using LlmContextCollector.Components.Pages.HomePanels;
using LlmContextCollector.Models;
using LlmContextCollector.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
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
        private IClipboard Clipboard { get; set; } = null!;

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
        private ProjectSettingsService ProjectSettingsService { get; set; } = null!;
        [Inject]
        private LocalizationService LocalizationService { get; set; } = null!;
        [Inject]
        private AcceptedResponseHistoryService AcceptedResponseHistoryService { get; set; } = null!;


        private WorkbenchPanel? _workbenchPanelRef;
        private ContextTab? _contextTabRef;

        private List<string> _selectedInContextList = new();
        private FileNode? _lastInteractionNode;

        private bool isPromptManagerVisible = false;

        private bool _isSettingsDialogVisible = false;
        private bool _isAzureDevOpsDialogVisible = false;
        private bool _isExclusionsDialogVisible = false;
        private bool _isLocPathDialogVisible = false;
        private DiffResultArgs? _pendingLocDiffArgs;

        private bool _isAttachableDocDialogVisible = false;
        private AttachableDocument? _editingAttachableDoc;

        private bool _showTreeContextMenu = false;
        private bool _showListContextMenu = false;
        private FileNode? _selectedNodeForMenu;
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
                await Task.Yield();
                await LoadHistoryEntry(latest);
            }
            else
            {
                AppState.StatusText = "Készen áll. Nincs betölthető előzmény.";
            }
        }

        private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            InvokeAsync(StateHasChanged);
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


        private string GetRelativeNodePath(FileNode node)
        {
            return Path.GetRelativePath(AppState.ProjectRoot, node.FullPath).Replace('\\', '/');
        }



        public async Task HandleNodeClick((FileNode Node, MouseEventArgs Args) payload)
        {
            var node = payload.Node;
            var e = payload.Args;

            if (e != null)
            {
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

                        if (!e.CtrlKey)
                        {
                            Utils.FileTreeHelper.DeselectAllNodes(AppState.FileTree);
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
                    Utils.FileTreeHelper.DeselectAllNodes(AppState.FileTree);
                    node.IsSelectedInTree = true;
                    _lastInteractionNode = node;
                }
            }
            else
            {
                Utils.FileTreeHelper.DeselectAllNodes(AppState.FileTree);
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

            var selectedNodesInTree = new List<FileNode>();
            Utils.FileTreeHelper.FindSelectedNodes(AppState.FileTree, selectedNodesInTree);

            var selectedPathsFromTree = selectedNodesInTree
                .Where(n => !n.IsDirectory)
                .Select(n => GetRelativeNodePath(n))
                .ToList();

            _selectedInContextList = selectedPathsFromTree
                .Intersect(AppState.SelectedFilesForContext)
                .ToList();

            if (selectedNodesInTree.Count == 1 && !selectedNodesInTree[0].IsDirectory)
            {
                var relativePath = GetRelativeNodePath(selectedNodesInTree[0]);
                AppState.PreviewSearchTerm = AppState.SearchTerm;
                AppState.CurrentPreviewPath = relativePath;
            }
            else
            {
                AppState.CurrentPreviewPath = string.Empty;
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
            Utils.FileTreeHelper.DeselectAllNodes(AppState.FileTree);

            if (_selectedInContextList.Any() && !string.IsNullOrEmpty(AppState.ProjectRoot))
            {
                FileNode? focusNode = null;
                
                foreach (var relPath in _selectedInContextList)
                {
                    if (relPath.StartsWith("[ADO]") || relPath.StartsWith("[ORIGINAL]")) continue;
                    
                    var fullPath = Path.Combine(AppState.ProjectRoot, relPath);
                    var node = AppState.FindNodeByPath(fullPath);
                    
                    if (node != null)
                    {
                        node.IsSelectedInTree = true;
                        AppState.ExpandNodeParents(node);
                        focusNode = node;
                    }
                }

                AppState.NotifyStateChanged(nameof(AppState.FileTree));

                if (focusNode != null && selectedFiles.Count == 1)
                {
                    _lastInteractionNode = focusNode;
                    await Task.Delay(50); 
                    
                    var elementId = "filenode-" + Convert.ToBase64String(Encoding.UTF8.GetBytes(focusNode.FullPath))
                        .Replace("=", "")
                        .Replace("+", "-")
                        .Replace("/", "_");
                    
                    await JSRuntime.InvokeVoidAsync("scrollToElement", elementId);
                }
            }
            else
            {
                AppState.NotifyStateChanged(nameof(AppState.FileTree));
            }

            AppState.CurrentPreviewPath = selectedFiles.Count == 1 ? selectedFiles.First() : string.Empty;

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

        #region Context Menus & Exclude

        private async Task HandleTreeContextMenu((FileNode? Node, MouseEventArgs Args) payload)
        {
            var node = payload.Node;
            var e = payload.Args;

            if (node != null && !node.IsSelectedInTree)
            {
                await HandleNodeClick((node, e));
            }

            var selectedNodes = new List<FileNode>();
            Utils.FileTreeHelper.FindSelectedNodes(AppState.FileTree, selectedNodes);

            if (selectedNodes.Any())
            {
                _selectedNodeForMenu = node ?? selectedNodes.LastOrDefault();
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
                _selectedNodeForMenu = null;
                StateHasChanged();
            }
            _workbenchPanelRef?.CloseCustomDropdowns();
        }

        private async Task SetAsBuildTarget()
        {
            if (_selectedNodeForMenu == null) return;

            var relPath = GetRelativeNodePath(_selectedNodeForMenu);
            AppState.DefaultBuildCommand = $"dotnet build {relPath}";
            AppState.DefaultRunCommand = $"dotnet run --project {relPath}";

            await ProjectSettingsService.SaveSettingsForProjectAsync(AppState.ProjectRoot);
            AppState.StatusText = $"Build célpont beállítva: {_selectedNodeForMenu.Name}";
            HideContextMenus();
        }

        private async Task ExcludeSelectedFromTree()
        {
            HideContextMenus();
            var selectedNodes = new List<FileNode>();
            Utils.FileTreeHelper.FindSelectedNodes(AppState.FileTree, selectedNodes);

            if (!selectedNodes.Any()) return;

            var newIgnores = new List<string>();
            foreach (var node in selectedNodes)
            {
                newIgnores.Add(GetRelativeNodePath(node));
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
        }

        private async Task CopySelectedNodeName()
        {
            HideContextMenus();
            var selectedNodes = new List<FileNode>();
            Utils.FileTreeHelper.FindSelectedNodes(AppState.FileTree, selectedNodes);

            if (selectedNodes.Count == 1)
            {
                await Clipboard.SetTextAsync(selectedNodes.First().Name);
                AppState.StatusText = $"Név másolva: {selectedNodes.First().Name}";
            }
        }

        private async Task CopySelectedNodePath()
        {
            HideContextMenus();
            var selectedNodes = new List<FileNode>();
            Utils.FileTreeHelper.FindSelectedNodes(AppState.FileTree, selectedNodes);

            if (selectedNodes.Count == 1)
            {
                var node = selectedNodes.First();
                var relativePath = GetRelativeNodePath(node);
                await Clipboard.SetTextAsync(relativePath);
                AppState.StatusText = $"Útvonal másolva: {relativePath}";
            }
        }

        private async Task CopySelectedFilesContentFromTree()
        {
            HideContextMenus();
            var selectedNodes = new List<FileNode>();
            Utils.FileTreeHelper.FindSelectedNodes(AppState.FileTree, selectedNodes);

            var items = new List<(string FullPath, string DisplayPath)>();
            foreach (var node in selectedNodes)
            {
                CollectFilesRecursive(node, items);
            }

            var distinctItems = items.GroupBy(x => x.FullPath).Select(g => g.First()).ToList();

            await CopyFilesContentToClipboard(distinctItems);
        }

        private void CollectFilesRecursive(FileNode node, List<(string FullPath, string DisplayPath)> items)
        {
            if (node.IsDirectory)
            {
                foreach (var child in node.Children)
                {
                    CollectFilesRecursive(child, items);
                }
            }
            else
            {
                items.Add((node.FullPath, GetRelativeNodePath(node)));
            }
        }

        private async Task CopySelectedFilesContentFromList()
        {
            HideContextMenus();
            if (!_selectedInContextList.Any()) return;

            var items = new List<(string FullPath, string DisplayPath)>();
            foreach (var relPath in _selectedInContextList)
            {
                string? fullPath = null;
                if (relPath.StartsWith("[ADO]"))
                {
                    if (!string.IsNullOrEmpty(AppState.AdoDocsPath))
                    {
                        fullPath = Path.Combine(AppState.AdoDocsPath, relPath.Substring(5));
                    }
                }
                else if (!string.IsNullOrEmpty(AppState.ProjectRoot))
                {
                    fullPath = Path.Combine(AppState.ProjectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                }

                if (fullPath != null) items.Add((fullPath, relPath));
            }

            await CopyFilesContentToClipboard(items);
        }

        private async Task CopyFilesContentToClipboard(List<(string FullPath, string DisplayPath)> items)
        {
            if (!items.Any()) return;

            try
            {
                var sb = new StringBuilder();
                foreach (var item in items)
                {
                    if (File.Exists(item.FullPath))
                    {
                        sb.AppendLine($"// --- Fájl: {item.DisplayPath} ---");
                        sb.AppendLine(await File.ReadAllTextAsync(item.FullPath));
                        sb.AppendLine();
                    }
                }

                if (sb.Length > 0)
                {
                    await Clipboard.SetTextAsync(sb.ToString().Trim());
                    AppState.StatusText = $"{items.Count} fájl tartalma másolva a vágólapra.";
                }
            }
            catch (Exception ex)
            {
                AppState.StatusText = $"Hiba a tartalom másolása közben: {ex.Message}";
            }
        }

        #endregion


        #region History

        private async Task LoadHistoryEntry(HistoryEntry entry)
        {
            await HistoryManagerService.ApplyHistoryEntryAsync(entry);
            _selectedInContextList = AppState.SelectedFilesForContext.ToList();
            
            AppState.CurrentPreviewPath = string.Empty;
            
            StateHasChanged();
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
            
            AppState.AiModels.Clear();
            if (settings.AiModels != null)
            {
                foreach (var model in settings.AiModels) AppState.AiModels.Add(model);
            }
            
            AppState.GitSuggestionModelId = settings.GitSuggestionModelId;
            AppState.ChatModelId = settings.ChatModelId;
            AppState.AgentModelId = settings.AgentModelId;

            AppState.OllamaApiUrl = settings.OllamaApiUrl ?? "http://localhost:11434/v1/";
            AppState.OllamaModel = settings.OllamaModel ?? "qwen2.5:7b-instruct";
            AppState.OllamaShowThinking = settings.OllamaShowThinking;

            AppState.AzureDevOpsOrganizationUrl = settings.AzureDevOpsOrganizationUrl;
            AppState.AzureDevOpsProject = settings.AzureDevOpsProject;
            AppState.AzureDevOpsIterationPath = settings.AzureDevOpsIterationPath;
            AppState.AzureDevOpsPat = settings.AzureDevOpsPat;
            AppState.AdoDownloadOnlyMine = settings.AdoDownloadOnlyMine;
            
            AppState.DefaultBuildCommand = settings.BuildCommand ?? "dotnet build";
            AppState.DefaultRunCommand = settings.RunCommand ?? "dotnet run";
            AppState.LogInformationLevel = settings.LogInformationLevel;
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

        private async Task HandleChatApplyResponse(string content)
        {
            await _workbenchPanelRef!.RouteResponseAsync(content);
        }

        private void HandleRequestLocPath(DiffResultArgs args)
        {
            _pendingLocDiffArgs = args;
            _isLocPathDialogVisible = true;
            StateHasChanged();
        }

        private async Task HandleLocalizationPathSet(string path)
        {
            AppState.LocalizationResourcePath = path;
            _isLocPathDialogVisible = false;
            await AzureDevOpsService.SaveSettingsForCurrentProjectAsync();

            if (_pendingLocDiffArgs != null)
            {
                var locChanges = _pendingLocDiffArgs.DiffResults.Where(r => r.Path.StartsWith("[LOC]")).ToList();
                int added = 0;

                if (locChanges.Any())
                {
                    var sb = new StringBuilder();
                    foreach (var loc in locChanges)
                    {
                        var key = loc.Path.Substring(6);
                        sb.AppendLine($"  <data name=\"{key}\" xml:space=\"preserve\"><value>{loc.NewContent}</value></data>");
                    }
                    added = await LocalizationService.UpdateResourceFileAsync(path, sb.ToString());
                }

                AppState.StatusText = $"Lokalizáció mentve ({added} kulcs).";
                var remainingFiles = _pendingLocDiffArgs.DiffResults.Where(r => !r.Path.StartsWith("[LOC]")).ToList();
                if (remainingFiles.Any())
                {
                    ShowDiffDialog(new DiffResultArgs(_pendingLocDiffArgs.GlobalExplanation, remainingFiles, _pendingLocDiffArgs.FullLlmResponse));
                }
                _pendingLocDiffArgs = null;
            }
            StateHasChanged();
        }

        public void ShowExclusionsDialog()
        {
            _isExclusionsDialogVisible = true;
            StateHasChanged();
        }

        private void OnExclusionsDialogClose()
        {
            _isExclusionsDialogVisible = false;
            StateHasChanged();
        }

        public void ShowAttachableDocDialog(AttachableDocument? doc = null)
        {
            _editingAttachableDoc = doc;
            _isAttachableDocDialogVisible = true;
            StateHasChanged();
        }

        private void OnAttachableDocDialogClose()
        {
            _isAttachableDocDialogVisible = false;
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
            if (string.IsNullOrWhiteSpace(AppState.ProjectRoot)) return;

            AppState.ShowLoading("Azure DevOps work item-ek letöltése...");
            try
            {
                await AzureDevOpsService.SaveSettingsForCurrentProjectAsync();
                await AzureDevOpsService.DownloadWorkItemsAsync(
                    AppState.AzureDevOpsOrganizationUrl, AppState.AzureDevOpsProject,
                    AppState.AzureDevOpsPat, 
                    AppState.AzureDevOpsIterationPath, AppState.ProjectRoot,
                    isIncremental, AppState.AdoDownloadOnlyMine);

                await AzureDevOpsService.SaveSettingsForCurrentProjectAsync(DateTime.UtcNow);
                AzureDevOpsService.UpdateAdoPaths(AppState.ProjectRoot);
                AppState.StatusText = "Azure DevOps work item-ek sikeresen letöltve.";
            }
            catch (Exception ex)
            {
                await JSRuntime.InvokeVoidAsync("alert", $"Hiba a letöltéskor: {ex.Message}");
            }
            finally
            {
                AppState.HideLoading();
            }
        }


        private async Task ProcessGitDiffAsync()
        {
            var args = await GitWorkflowService.PrepareGitDiffForReviewAsync(AppState.PromptText);
            ShowDiffDialog(args);
        }

        private void ShowDiffDialog(DiffResultArgs args)
        {
            AppState.DiffGlobalExplanation = args.GlobalExplanation;
            AppState.DiffResults = args.DiffResults;
            AppState.DiffFullLlmResponse = args.FullLlmResponse;
            AppState.DiffOriginalPrompt = args.OriginalPrompt;
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
            var locChanges = acceptedResults.Where(r => r.Path.StartsWith("[LOC]")).ToList();
            var fileChanges = acceptedResults.Where(r => !r.Path.StartsWith("[LOC]")).ToList();

            int locAddedCount = 0;
            if (locChanges.Any() && !string.IsNullOrEmpty(AppState.LocalizationResourcePath))
            {
                var sb = new StringBuilder();
                foreach (var loc in locChanges)
                {
                    var key = loc.Path.Substring(6);
                    sb.AppendLine($"  <data name=\"{key}\" xml:space=\"preserve\"><value>{loc.NewContent}</value></data>");
                }
                locAddedCount = await LocalizationService.UpdateResourceFileAsync(AppState.LocalizationResourcePath, sb.ToString());
            }

            var (acceptedCount, errorCount) = await GitWorkflowService.AcceptChangesAsync(fileChanges);

            if ((acceptedCount > 0 || locAddedCount > 0) && !string.IsNullOrEmpty(AppState.ProjectRoot))
            {
                await AcceptedResponseHistoryService.AddEntryAsync(AppState.ProjectRoot, AppState.DiffGlobalExplanation, acceptedResults);
            }

            AppState.StatusText = $"Változások elfogadva. Fájlok: {acceptedCount}, Lokalizáció: {locAddedCount}.";
            StateHasChanged();
        }

        private async Task HandleCreateBranchAsync(string branchName)
        {
            AppState.ShowLoading($"'{branchName}' branch létrehozása...");
            try { await GitWorkflowService.CreateAndCheckoutBranchAsync(branchName); }
            finally { AppState.HideLoading(); }
        }

        private async Task HandleCommitAsync(CommitAndPushArgs args)
        {
            AppState.ShowLoading("Változások commitolása...");
            try { await GitWorkflowService.CommitChangesAsync(args); }
            finally { AppState.HideLoading(); }
        }

        private async Task HandlePushAsync(CommitAndPushArgs args)
        {
            AppState.ShowLoading("Változások pusholása...");
            try { await GitWorkflowService.PushChangesAsync(args.BranchName); await OnDiffDialogClose(); }
            finally { AppState.HideLoading(); }
        }

        #endregion



        #region Panel Resizing
        private string _activeSplitter = "None";
        private bool _isResizing = false;
        private double _startX, _startY, _startLeftFlex, _startMiddleFlex, _startRightFlex, _startTopFlex, _startMidFlex, _startBotFlex;
        private double _containerWidth = 1000, _containerHeight = 800;

        private async Task StartResize(MouseEventArgs e, string splitter)
        {
            _isResizing = true;
            _activeSplitter = splitter;
            _startX = e.ClientX;
            _startY = e.ClientY;
            try
            {
                _containerWidth = await JSRuntime.InvokeAsync<double>("eval", "window.innerWidth");
                _containerHeight = await JSRuntime.InvokeAsync<double>("eval", "window.innerHeight");
            }
            catch { }
            _startLeftFlex = AppState.LeftPanelFlex; 
            _startMiddleFlex = AppState.MiddlePanelFlex;
            _startRightFlex = AppState.RightPanelFlex;
            _startTopFlex = AppState.RightTopPanelFlex; 
            _startMidFlex = AppState.RightMiddlePanelFlex; 
            _startBotFlex = AppState.RightBottomPanelFlex;
        }

        private void StopResize() { _isResizing = false; _activeSplitter = "None"; }

        private void OnMouseMove(MouseEventArgs e)
        {
            if (!_isResizing) return;
            double flexFactorX = 100.0 / Math.Max(1, _containerWidth);
            double flexFactorY = 100.0 / Math.Max(1, _containerHeight);

            switch (_activeSplitter)
            {
                case "LeftMiddle":
                    {
                        var delta = (e.ClientX - _startX) * flexFactorX;
                        // A bal és középső panel közötti splitter mozgatása. 
                        // Az összegüknek (StartLeft + StartMiddle) állandónak kell maradnia.
                        var combinedFlex = _startLeftFlex + _startMiddleFlex;
                        var newLeft = Math.Clamp(_startLeftFlex + delta, 10, combinedFlex - 10);
                        
                        AppState.LeftPanelFlex = newLeft;
                        AppState.MiddlePanelFlex = combinedFlex - newLeft;
                    }
                    break;
                case "MiddleRight":
                    {
                        var delta = (e.ClientX - _startX) * flexFactorX;
                        // A középső és jobb panel közötti splitter mozgatása.
                        // Az összegüknek (StartMiddle + StartRight) állandónak kell maradnia.
                        var combinedFlex = _startMiddleFlex + _startRightFlex;
                        var newMiddle = Math.Clamp(_startMiddleFlex + delta, 10, combinedFlex - 10);

                        AppState.MiddlePanelFlex = newMiddle;
                        AppState.RightPanelFlex = combinedFlex - newMiddle;
                    }
                    break;
                case "RightTop":
                    var deltaY1 = (e.ClientY - _startY) * flexFactorY;
                    AppState.RightTopPanelFlex = Math.Clamp(_startTopFlex + deltaY1, 10, 80);
                    AppState.RightMiddlePanelFlex = Math.Clamp(_startMidFlex - deltaY1, 10, 80);
                    break;
                case "RightMiddle":
                    var deltaY2 = (e.ClientY - _startY) * flexFactorY;
                    AppState.RightMiddlePanelFlex = Math.Clamp(_startMidFlex + deltaY2, 10, 80);
                    AppState.RightBottomPanelFlex = Math.Clamp(_startBotFlex - deltaY2, 10, 80);
                    break;
            }
        }
        #endregion

        public void Dispose()
        {
            AppState.PropertyChanged -= OnAppStateChanged;
        }
    }
}