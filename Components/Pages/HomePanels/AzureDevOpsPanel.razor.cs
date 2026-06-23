using LlmContextCollector.Models;
using LlmContextCollector.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace LlmContextCollector.Components.Pages.HomePanels
{
    public partial class AzureDevOpsPanel
    {
        [Inject] private SettingsService SettingsService { get; set; } = null!;
        [Inject] private IClipboard Clipboard { get; set; } = null!;
        [Inject] private GitWorkflowService GitWorkflowService { get; set; } = null!;
        [Inject] private ProjectService ProjectService { get; set; } = null!;

        private List<string> _availableStatuses = new() { "New", "Active", "Resolved", "Closed", "Committed", "Proposed" };
        private bool _isReportModalVisible = false;
        private string _reportText = string.Empty;
        private string _reportJson = string.Empty;
        private bool _isGeneratingReport = false;
        private List<string> _selectedStatuses = new() { "Active", "Resolved" };
        private List<string> _selectedAssignees = new() { "@Me" };
        private List<AdoIdentity> _projectMembers = new();
        private string _selectedType = "";
        private bool _isLoading = false;
        private bool _isLoadingMembers = false;
        private List<WorkItemSearchResult> _workItems = new();
        private List<GitPullRequest> _activePrs = new();
        private bool _showPrList = false;

        protected override async Task OnInitializedAsync()
        {
            await LoadMembers();
        }

        private async Task ToggleMode(bool showPrs)
        {
            _showPrList = showPrs;
            if (_showPrList)
            {
                await LoadActivePullRequests();
            }
            else
            {
                await LoadWorkItems();
            }
        }

        private async Task LoadActivePullRequests()
        {
            _isLoading = true;
            try
            {
                _activePrs = await AdoService.GetActivePullRequestsAsync();
            }
            catch (Exception ex)
            {
                LogService.LogError("ADO Panel", "Hiba az aktív PR-ek betöltésekor", ex.Message);
            }
            finally
            {
                _isLoading = false;
            }
        }

        private async Task LoadMembers()
        {
            _isLoadingMembers = true;
            try
            {
                _projectMembers = await AdoService.GetProjectMembersAsync();
                if (!_projectMembers.Any() && !string.IsNullOrWhiteSpace(AppState.AzureDevOpsOrganizationUrl))
                {
                    AppState.StatusText = "Nem sikerült betölteni az Azure DevOps projekt tagjait. Ellenőrizze a kapcsolatot és a beállításokat.";
                }
            }
            catch (Exception ex)
            {
                LogService.LogError("ADO Panel", "Hiba a tagok betöltésekor", ex.Message);
                AppState.StatusText = "Hiba történt az Azure DevOps tagok betöltése közben.";
            }
            finally
            {
                _isLoadingMembers = false;
                StateHasChanged();
            }
        }

        private void ToggleStatus(string status)
        {
            if (_selectedStatuses.Contains(status)) _selectedStatuses.Remove(status);
            else _selectedStatuses.Add(status);
        }

        private void ToggleAssignee(string name)
        {
            if (_selectedAssignees.Contains(name)) _selectedAssignees.Remove(name);
            else _selectedAssignees.Add(name);
        }

        private void OnMinDateChanged(ChangeEventArgs e)
        {
            if (DateTime.TryParse(e.Value?.ToString(), out var dt))
            {
                AppState.AdoMinChangedDate = dt;
            }
            else
            {
                AppState.AdoMinChangedDate = null;
            }
        }

        private async Task SaveAdoPanelSettingsAsync()
        {
            try
            {
                var settings = await SettingsService.GetSettingsAsync();
                settings.AdoDownloadOnlyMine = AppState.AdoDownloadOnlyMine;
                settings.AdoMinChangedDate = AppState.AdoMinChangedDate;
                await SettingsService.SaveSettingsAsync(settings);
            }
            catch { }
        }

        private async Task LoadWorkItems()
        {
            _isLoading = true;
            try
            {
                await SaveAdoPanelSettingsAsync();
                _workItems = await AdoService.SearchWorkItemsAsync(_selectedStatuses, _selectedAssignees, _selectedType);
            }
            catch (Exception ex)
            {
                LogService.LogError("ADO Panel", "Hiba a kereséskor", ex.Message);
            }
            finally
            {
                _isLoading = false;
            }
        }

        private bool _isDetailsModalVisible = false;
        private int _editingItemId;
        private string _editingItemText = string.Empty;
        private List<AttachedImage> _editingItemImages = new();
        private string _editingState = string.Empty;
        private double? _editingRemainingWork;
        private double? _editingCompletedWork;
        private double? _editingOriginalEstimate;
        private double? _editingStoryPoints;
        private int? _editingPriority;
        private string _editingSeverity = string.Empty;
        private DateTime? _editingTargetDate;
        private string _newCommentText = string.Empty;
        private bool _isSavingAdo = false;
        private List<AzureDevOpsService.LinkedPrInfo> _linkedPrs = new();

        private async Task SendToPromptDirectly(int id)
        {
            AppState.ShowLoading($"Munkaelem #{id} letöltése...");
            try
            {
                var res = await AdoService.GetFormattedWorkItemAsync(id, AppState.AttachedImages.Count);
                if (!string.IsNullOrEmpty(res.Text))
                {
                    AppState.PromptText = res.Text + "\n\n" + AppState.PromptText;
                    foreach (var img in res.Images) AppState.AttachedImages.Add(img);
                    AppState.StatusText = $"Munkaelem #{id} betöltve a promptba.";
                }
            }
            catch (Exception ex)
            {
                LogService.LogError("ADO Panel", $"Hiba a letöltéskor (#{id})", ex.Message);
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        private async Task OpenDetailsDialog(int id)
        {
            AppState.ShowLoading($"Munkaelem #{id} letöltése...");
            try
            {
                var rawWi = await AdoService.GetRawWorkItemAsync(id);
                var formatted = await AdoService.GetFormattedWorkItemAsync(id, AppState.AttachedImages.Count);

                if (formatted.Text != null)
                {
                    _editingItemId = id;
                    _editingItemText = formatted.Text;
                    _editingItemImages = formatted.Images ?? new List<AttachedImage>();
                    _newCommentText = string.Empty;
                    _linkedPrs.Clear();

                    if (rawWi != null)
                    {
                        _editingState = GetFieldFromRaw(rawWi, "System.State");

                        if (double.TryParse(GetFieldFromRaw(rawWi, "Microsoft.VSTS.Scheduling.RemainingWork"), out var rem))
                            _editingRemainingWork = rem;
                        else
                            _editingRemainingWork = null;

                        if (double.TryParse(GetFieldFromRaw(rawWi, "Microsoft.VSTS.Scheduling.CompletedWork"), out var comp))
                            _editingCompletedWork = comp;
                        else
                            _editingCompletedWork = null;

                        if (double.TryParse(GetFieldFromRaw(rawWi, "Microsoft.VSTS.Scheduling.OriginalEstimate"), out var orig))
                            _editingOriginalEstimate = orig;
                        else
                            _editingOriginalEstimate = null;

                        if (double.TryParse(GetFieldFromRaw(rawWi, "Microsoft.VSTS.Scheduling.StoryPoints"), out var sp))
                            _editingStoryPoints = sp;
                        else
                            _editingStoryPoints = null;

                        if (int.TryParse(GetFieldFromRaw(rawWi, "Microsoft.VSTS.Common.Priority"), out var prio))
                            _editingPriority = prio;
                        else
                            _editingPriority = null;

                        _editingSeverity = GetFieldFromRaw(rawWi, "Microsoft.VSTS.Common.Severity");

                        if (DateTime.TryParse(GetFieldFromRaw(rawWi, "Microsoft.VSTS.Scheduling.TargetDate"), out var td))
                            _editingTargetDate = td;
                        else
                            _editingTargetDate = null;

                        if (rawWi.Relations != null)
                        {
                            var prIds = new List<int>();
                            foreach (var rel in rawWi.Relations)
                            {
                                if (rel.Rel == "ArtifactLink" && rel.Url.Contains("PullRequestId", StringComparison.OrdinalIgnoreCase))
                                {
                                    var decodedUrl = Uri.UnescapeDataString(rel.Url);
                                    var lastSlash = decodedUrl.LastIndexOf('/');
                                    if (lastSlash != -1 && int.TryParse(decodedUrl.Substring(lastSlash + 1), out var prId))
                                    {
                                        prIds.Add(prId);
                                    }
                                }
                            }

                            foreach (var prId in prIds.Distinct())
                            {
                                var prInfo = await AdoService.GetPullRequestDetailsAsync(prId);
                                if (prInfo != null)
                                {
                                    _linkedPrs.Add(prInfo);
                                }
                            }
                        }
                    }

                    _isDetailsModalVisible = true;
                }
            }
            catch (Exception ex)
            {
                LogService.LogError("ADO Panel", $"Hiba a részletek letöltésekor ({id})", ex.Message);
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        private void AddPrFilesToContext(AzureDevOpsService.LinkedPrInfo pr)
        {
            if (pr == null || pr.Files == null || string.IsNullOrEmpty(AppState.ProjectRoot))
            {
                return;
            }

            int addedCount = 0;
            foreach (var file in pr.Files)
            {
                if (file == null || string.IsNullOrEmpty(file.Path)) continue;

                var cleanPath = file.Path.TrimStart('/');
                var node = AppState.FindNodeByPath(Path.Combine(AppState.ProjectRoot, cleanPath));

                if (node == null)
                {
                    var fileName = Path.GetFileName(cleanPath);
                    if (string.IsNullOrEmpty(fileName)) continue;

                    var flatNodes = new List<FileNode>();
                    Utils.FileTreeHelper.GetAllFileNodes(AppState.FileTree, flatNodes);
                    node = flatNodes.FirstOrDefault(n => n != null && n.Name != null && n.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                }

                if (node != null && !string.IsNullOrEmpty(node.FullPath))
                {
                    var relPath = Path.GetRelativePath(AppState.ProjectRoot, node.FullPath).Replace('\\', '/');
                    var originalPath = $"[ORIGINAL]{relPath}";

                    if (!AppState.SelectedFilesForContext.Contains(relPath))
                    {
                        AppState.SelectedFilesForContext.Add(relPath);
                        addedCount++;
                    }

                    bool isNew = string.Equals(file.ChangeType, "add", StringComparison.OrdinalIgnoreCase);
                    if (!isNew && !AppState.SelectedFilesForContext.Contains(originalPath))
                    {
                        AppState.SelectedFilesForContext.Add(originalPath);
                        addedCount++;
                    }
                }
            }

            if (addedCount > 0)
            {
                AppState.SaveContextListState();
                AppState.StatusText = $"PR #{pr.Id} fájljai ({addedCount} db) hozzáadva a kontextushoz.";
            }
            else
            {
                AppState.StatusText = "A fájlok már a kontextusban vannak vagy nem találhatók a helyi projektben.";
            }
        }

        private async Task StartCodeReviewForPr(AzureDevOpsService.LinkedPrInfo pr)
        {
            AppState.ShowLoading($"PR #{pr.Id} lekérése Code Review-hoz...");
            try
            {
                var result = await AdoService.GetFormattedPullRequestAsync(pr.Id);
                if (result == null || string.IsNullOrEmpty(result.FormattedText))
                {
                    AppState.StatusText = "A Pull Request lekérése nem adott eredményt.";
                    return;
                }

                AppState.PromptText = result.FormattedText + "\n\n" + AppState.PromptText;

                if (result.Images != null)
                {
                    foreach (var img in result.Images) AppState.AttachedImages.Add(img);
                }

                if (!string.IsNullOrEmpty(result.SourceBranch) && AppState.CurrentGitBranch != result.SourceBranch)
                {
                    AppState.StatusText = $"Átváltás a(z) {result.SourceBranch} ágra...";
                    try
                    {
                        await GitWorkflowService.CheckoutBranchAsync(result.SourceBranch);
                        await ProjectService.ReloadProjectAsync(preserveSelection: true);
                    }
                    catch (Exception ex)
                    {
                        AppState.StatusText = $"Ágváltás sikertelen: {ex.Message}. Folytatás a jelenlegi ágon.";
                    }
                }

                var reviewerTemplate = AppState.PromptTemplates.FirstOrDefault(t => t.Title.Contains("Reviewer", StringComparison.OrdinalIgnoreCase));
                if (reviewerTemplate != null)
                {
                    AppState.ActiveGlobalPromptId = reviewerTemplate.Id;
                }

                AddPrFilesToContext(pr);
                AppState.StatusText = $"PR #{pr.Id} sikeresen előkészítve Code Review-hoz.";
                CloseDetailsModal();
            }
            catch (Exception ex)
            {
                AppState.StatusText = $"Hiba a Code Review előkészítésekor: {ex.Message}";
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        private async Task StartCodeReviewForPr(GitPullRequest pr)
        {
            AppState.ShowLoading($"PR #{pr.PullRequestId} lekérése Code Review-hoz...");
            try
            {
                var result = await AdoService.GetFormattedPullRequestAsync(pr.PullRequestId);
                if (result == null || string.IsNullOrEmpty(result.FormattedText))
                {
                    AppState.StatusText = "A Pull Request lekérése nem adott eredményt.";
                    return;
                }

                AppState.PromptText = result.FormattedText + "\n\n" + AppState.PromptText;

                if (result.Images != null)
                {
                    foreach (var img in result.Images) AppState.AttachedImages.Add(img);
                }

                var sourceBranchClean = pr.SourceRefName;
                if (sourceBranchClean.StartsWith("refs/heads/")) sourceBranchClean = sourceBranchClean.Substring(11);

                if (!string.IsNullOrEmpty(sourceBranchClean) && AppState.CurrentGitBranch != sourceBranchClean)
                {
                    AppState.StatusText = $"Átváltás a(z) {sourceBranchClean} ágra...";
                    try
                    {
                        await GitWorkflowService.CheckoutBranchAsync(sourceBranchClean);
                        await ProjectService.ReloadProjectAsync(preserveSelection: true);
                    }
                    catch (Exception ex)
                    {
                        AppState.StatusText = $"Ágváltás sikertelen: {ex.Message}. Folytatás a jelenlegi ágon.";
                    }
                }

                var reviewerTemplate = AppState.PromptTemplates.FirstOrDefault(t => t.Title.Contains("Reviewer", StringComparison.OrdinalIgnoreCase));
                if (reviewerTemplate != null)
                {
                    AppState.ActiveGlobalPromptId = reviewerTemplate.Id;
                }

                var prInfo = new AzureDevOpsService.LinkedPrInfo(pr.PullRequestId, pr.Title, sourceBranchClean, result.Files);
                AddPrFilesToContext(prInfo);
                AppState.StatusText = $"PR #{pr.PullRequestId} sikeresen előkészítve Code Review-hoz.";
            }
            catch (Exception ex)
            {
                AppState.StatusText = $"Hiba a Code Review előkészítésekor: {ex.Message}";
            }
            finally
            {
                AppState.HideLoading();
            }
        }

        private string GetFieldFromRaw(WorkItem item, string fieldName)
        {
            if (item.Fields.TryGetValue(fieldName, out var val))
            {
                if (val is System.Text.Json.JsonElement elem)
                {
                    if (elem.ValueKind == System.Text.Json.JsonValueKind.Number) return elem.GetDouble().ToString();
                    return elem.GetString() ?? "";
                }
                return val.ToString() ?? "";
            }
            return "";
        }

        private void OnTargetDateChanged(ChangeEventArgs e)
        {
            if (DateTime.TryParse(e.Value?.ToString(), out var dt))
            {
                _editingTargetDate = dt;
            }
            else
            {
                _editingTargetDate = null;
            }
        }

        private async Task SaveAdoChanges()
        {
            _isSavingAdo = true;
            try
            {
                var fieldsSuccess = await AdoService.UpdateWorkItemFieldsAsync(
                    _editingItemId, 
                    _editingState, 
                    _editingRemainingWork, 
                    _editingCompletedWork,
                    _editingOriginalEstimate,
                    _editingStoryPoints,
                    _editingPriority,
                    _editingSeverity,
                    _editingTargetDate);

                var commentSuccess = true;

                if (!string.IsNullOrWhiteSpace(_newCommentText))
                {
                    commentSuccess = await AdoService.AddWorkItemCommentAsync(_editingItemId, _newCommentText);
                    if (commentSuccess)
                    {
                        _newCommentText = string.Empty;
                    }
                }

                if (fieldsSuccess && commentSuccess)
                {
                    AppState.StatusText = $"Munkaminta #{_editingItemId} frissítve az Azure DevOps-ban.";

                    var formatted = await AdoService.GetFormattedWorkItemAsync(_editingItemId, AppState.AttachedImages.Count);
                    _editingItemText = formatted.Text;
                    _editingItemImages = formatted.Images ?? new List<AttachedImage>();
                }
                else
                {
                    AppState.StatusText = "Hiba történt az Azure DevOps frissítés során.";
                }
            }
            catch (Exception ex)
            {
                LogService.LogError("ADO Panel", $"Hiba a mentéskor ({_editingItemId})", ex.Message);
            }
            finally
            {
                _isSavingAdo = false;
            }
        }

        private void CloseDetailsModal()
        {
            _isDetailsModalVisible = false;
            _editingItemText = string.Empty;
            _editingItemImages.Clear();
            _newCommentText = string.Empty;
        }

        private void SendEditingToPrompt()
        {
            if (!string.IsNullOrEmpty(_editingItemText))
            {
                AppState.PromptText = _editingItemText + "\n\n" + AppState.PromptText;
                foreach (var img in _editingItemImages)
                {
                    AppState.AttachedImages.Add(img);
                }
                AppState.StatusText = $"Szerkesztett munkaelem #{_editingItemId} betöltve a promptba.";
            }
            CloseDetailsModal();
        }

        private async Task DownloadAllWorkItems()
        {
            if (string.IsNullOrWhiteSpace(AppState.ProjectRoot)) return;

            _isLoading = true;
            AppState.ShowLoading("Munkaelemek letöltése...");
            try
            {
                await SaveAdoPanelSettingsAsync();
                await AdoService.SaveSettingsForCurrentProjectAsync();
                await AdoService.DownloadWorkItemsAsync(
                    AppState.AzureDevOpsOrganizationUrl, AppState.AzureDevOpsProject,
                    AppState.AzureDevOpsPat,
                    AppState.AzureDevOpsIterationPath, AppState.ProjectRoot,
                    isIncremental: false,
                    AppState.AdoDownloadOnlyMine);

                await AdoService.SaveSettingsForCurrentProjectAsync(DateTime.UtcNow);
                AdoService.UpdateAdoPaths(AppState.ProjectRoot);
                AppState.StatusText = "Munkaelemek letöltve a helyi kontextusba.";
            }
            catch (Exception ex)
            {
                LogService.LogError("ADO Panel", "Hiba a letöltéskor", ex.Message);
                AppState.StatusText = $"Hiba: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                AppState.HideLoading();
            }
        }

        private async Task GenerateReportAsync()
        {
            _isGeneratingReport = true;
            AppState.ShowLoading("Riport generálása...");
            try
            {
                await SaveAdoPanelSettingsAsync();
                var report = await AdoService.GenerateWorkReportAsync(_selectedAssignees, AppState.AdoMinChangedDate);
                if (report != null)
                {
                    _reportText = report.FormattedText;
                    _reportJson = report.JsonData;
                    _isReportModalVisible = true;
                    AppState.StatusText = "Riport sikeresen legenerálva.";
                }
                else
                {
                    AppState.StatusText = "Nem sikerült a riport generálása.";
                }
            }
            catch (Exception ex)
            {
                LogService.LogError("ADO Panel", "Hiba a riport generálásakor", ex.Message);
                AppState.StatusText = $"Hiba: {ex.Message}";
            }
            finally
            {
                _isGeneratingReport = false;
                AppState.HideLoading();
            }
        }

        private void CloseReportModal()
        {
            _isReportModalVisible = false;
            _reportText = string.Empty;
            _reportJson = string.Empty;
        }

        private async Task CopyReportToClipboard()
        {
            if (!string.IsNullOrEmpty(_reportText))
            {
                await Clipboard.SetTextAsync(_reportText);
                AppState.StatusText = "Riport a vágólapra másolva.";
            }
        }

        private void SendReportToPromptForAi()
        {
            if (!string.IsNullOrEmpty(_reportText))
            {
                var analysisTemplate = "Kérlek elemezd az alábbi Azure DevOps és Git integrált riportot. Mivel az ADO-ban az órák és story pointok gyakran pontatlanok, vedd figyelembe a kísérő Git commit számokat, a módosított egyedi fájlok számát és a kódváltozás adatait is a fejlesztők tényleges aktivitásának megértéséhez! Készíts egy tömör, objektív elemzést a munkavégzésről!\n\n";
                AppState.PromptText = analysisTemplate + _reportText + "\n\n" + AppState.PromptText;
                AppState.StatusText = "Riport és elemzési felhívás betöltve a promptba.";
            }
            CloseReportModal();
        }
    }
}