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
using static LlmContextCollector.Components.Pages.HomePanels.ContextPanel;

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
        [Inject]
        private LocalizationService LocalizationService { get; set; } = null!;
        [Inject]
        private AcceptedResponseHistoryService AcceptedResponseHistoryService { get; set; } = null!;


        private ContextPanel? _contextPanelRef;

        private List<string> _selectedInContextList = new();
        private FileNode? _lastInteractionNode;

        private bool isPromptManagerVisible = false;

        private bool _isSettingsDialogVisible = false;
        private bool _isAzureDevOpsDialogVisible = false;
        private bool _isDocumentSearchDialogVisible = false;
        private bool _isLocalAiChatVisible = false;
        private bool _isExclusionsDialogVisible = false;
        private bool _isLocPathDialogVisible = false;
        private DiffResultArgs? _pendingLocDiffArgs;

        private bool _isAttachableDocDialogVisible = false;
        private AttachableDocument? _editingAttachableDoc;
        private string _localAiPrompt = string.Empty;
        private string _localAiSystem = string.Empty;
        private string _localAiFiles = string.Empty;

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


        private string GetRelativeNodePath(FileNode node)
        {
            return Path.GetRelativePath(AppState.ProjectRoot, node.FullPath).Replace('\\', '/');
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
                // Programmatic selection (e.g. search navigation)
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

            var selectedNodes = new List<FileNode>();
            Utils.FileTreeHelper.FindSelectedNodes(AppState.FileTree, selectedNodes);

            var selectedPathsFromTree = selectedNodes
                .Where(n => !n.IsDirectory)
                .Select(n => GetRelativeNodePath(n))
                .ToList();

            _selectedInContextList = selectedPathsFromTree
                .Intersect(AppState.SelectedFilesForContext)
                .ToList();

            if (_contextPanelRef != null)
            {
                if (selectedNodes.Count == 1 && !selectedNodes[0].IsDirectory)
                {
                    var relativePath = GetRelativeNodePath(selectedNodes[0]);
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
            Utils.FileTreeHelper.DeselectAllNodes(AppState.FileTree);

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

        #region Context Menus & Exclude

        private void ShowTreeContextMenu(MouseEventArgs e)
        {
            var selectedNodes = new List<FileNode>();
            Utils.FileTreeHelper.FindSelectedNodes(AppState.FileTree, selectedNodes);
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
            _contextPanelRef?.CloseCustomDropdowns();
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
            else
            {
                AppState.StatusText = "A kijelölt elemek már a kizárási listán vannak.";
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
            else
            {
                AppState.StatusText = "A név másolásához pontosan egy elemet kell kiválasztani.";
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
            else
            {
                AppState.StatusText = "Az elérési út másolásához pontosan egy elemet kell kiválasztani.";
            }
        }

        private async Task CopySelectedFilesContentFromTree()
        {
            HideContextMenus();
            var selectedNodes = new List<FileNode>();
            Utils.FileTreeHelper.FindSelectedNodes(AppState.FileTree, selectedNodes);

            var items = selectedNodes
                .Where(n => !n.IsDirectory)
                .Select(n => (
                    FullPath: n.FullPath, 
                    DisplayPath: GetRelativeNodePath(n)
                ))
                .ToList();

            if (!items.Any())
            {
                AppState.StatusText = "Nincs fájl kiválasztva.";
                return;
            }

            await CopyFilesContentToClipboard(items);
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

                if (fullPath != null)
                {
                    items.Add((fullPath, relPath));
                }
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
                else
                {
                    AppState.StatusText = "Nem sikerült tartalmat olvasni a kijelölt fájlokból.";
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
            AppState.OllamaApiUrl = settings.OllamaApiUrl;
            AppState.OllamaModel = settings.OllamaModel;
            AppState.UseOllamaEmbeddings = settings.UseOllamaEmbeddings;
            AppState.OllamaEmbeddingModel = settings.OllamaEmbeddingModel;
            AppState.AzureDevOpsOrganizationUrl = settings.AzureDevOpsOrganizationUrl;
            AppState.AzureDevOpsProject = settings.AzureDevOpsProject;
            AppState.AzureDevOpsIterationPath = settings.AzureDevOpsIterationPath;
            AppState.AzureDevOpsPat = settings.AzureDevOpsPat;
            AppState.AdoDownloadOnlyMine = settings.AdoDownloadOnlyMine;
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

        private void ShowLocalAiChat(LocalAiContextArgs args)
        {
            _localAiPrompt = args.Prompt;
            _localAiSystem = args.System;
            _localAiFiles = args.Files;
            _isLocalAiChatVisible = true;
            StateHasChanged();
        }

        private void OnLocalAiChatClose()
        {
            _isLocalAiChatVisible = false;
            _localAiPrompt = string.Empty;
            _localAiSystem = string.Empty;
            _localAiFiles = string.Empty;
            StateHasChanged();
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
                else if (!string.IsNullOrEmpty(_pendingLocDiffArgs.LocalizationData))
                {
                    added = await LocalizationService.UpdateResourceFileAsync(path, _pendingLocDiffArgs.LocalizationData);
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
            _editingAttachableDoc = null;
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
                    isIncremental,
                    AppState.AdoDownloadOnlyMine);

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
            if (locChanges.Any())
            {
                if (string.IsNullOrEmpty(AppState.LocalizationResourcePath))
                {
                    _pendingLocDiffArgs = new DiffResultArgs(AppState.DiffGlobalExplanation, locChanges, AppState.DiffFullLlmResponse);
                    _isLocPathDialogVisible = true;
                }
                else
                {
                    var sb = new StringBuilder();
                    foreach (var loc in locChanges)
                    {
                        var key = loc.Path.Substring(6);
                        sb.AppendLine($"  <data name=\"{key}\" xml:space=\"preserve\"><value>{loc.NewContent}</value></data>");
                    }
                    locAddedCount = await LocalizationService.UpdateResourceFileAsync(AppState.LocalizationResourcePath, sb.ToString());
                }
            }

            var (acceptedCount, errorCount) = await GitWorkflowService.AcceptChangesAsync(fileChanges);
            
            var historyFiles = acceptedResults.Select(r => new DiffResult 
            { 
                Path = r.Path, 
                OldContent = r.OldContent, 
                NewContent = r.NewContent, 
                Status = r.Status, 
                Explanation = r.Explanation 
            }).ToList();

            if ((acceptedCount > 0 || locAddedCount > 0) && !string.IsNullOrEmpty(AppState.ProjectRoot))
            {
                await AcceptedResponseHistoryService.AddEntryAsync(AppState.ProjectRoot, AppState.DiffGlobalExplanation, historyFiles);
            }

            AppState.StatusText = $"Változások elfogadása befejezve. Fájlok: {acceptedCount}, Lokalizáció: {locAddedCount}, Hiba: {errorCount}.";
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
            Utils.FileTreeHelper.GetAllFileNodes(AppState.FileTree, allFileNodes);
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

        private double _containerWidth = 1000;
        private double _containerHeight = 800;

        private async Task StartResize(MouseEventArgs e, string splitter)
        {
            _isResizing = true;
            _activeSplitter = splitter;
            _startX = e.ClientX;
            _startY = e.ClientY;

            // Lekérjük az aktuális ablakméreteket a pontos flex számításhoz
            try
            {
                _containerWidth = await JSRuntime.InvokeAsync<double>("eval", "window.innerWidth");
                _containerHeight = await JSRuntime.InvokeAsync<double>("eval", "window.innerHeight");
            }
            catch { /* Fallback az alapértelmezett értékekre */ }

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

            // A flexFactor az ablakméret és a teljes flex súly (100) aránya a folyamatos mozgáshoz
            double flexFactorX = 100.0 / Math.Max(1, _containerWidth);
            double flexFactorY = 100.0 / Math.Max(1, _containerHeight);

            switch (_activeSplitter)
            {
                case "LeftMiddle":
                    var flexDeltaX1 = deltaX * flexFactorX;
                    AppState.LeftPanelFlex = Math.Clamp(_startLeftFlex + flexDeltaX1, 10, 80);
                    AppState.MiddlePanelFlex = Math.Clamp(_startMiddleFlex - flexDeltaX1, 10, 80);
                    break;

                case "MiddleRight":
                    var flexDeltaX2 = deltaX * flexFactorX;
                    AppState.MiddlePanelFlex = Math.Clamp(_startMiddleFlex + flexDeltaX2, 10, 80);
                    AppState.RightPanelFlex = Math.Clamp(_startRightFlex - flexDeltaX2, 10, 80);
                    break;

                case "RightTop":
                    var flexDeltaY1 = deltaY * flexFactorY;
                    AppState.RightTopPanelFlex = Math.Clamp(_startTopFlex + flexDeltaY1, 10, 80);
                    AppState.RightMiddlePanelFlex = Math.Clamp(_startMidFlex - flexDeltaY1, 10, 80);
                    break;

                case "RightMiddle":
                    var flexDeltaY2 = deltaY * flexFactorY;
                    AppState.RightMiddlePanelFlex = Math.Clamp(_startMidFlex + flexDeltaY2, 10, 80);
                    AppState.RightBottomPanelFlex = Math.Clamp(_startBotFlex - flexDeltaY2, 10, 80);
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