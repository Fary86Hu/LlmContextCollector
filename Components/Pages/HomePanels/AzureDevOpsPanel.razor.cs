using LlmContextCollector.Models;
using LlmContextCollector.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace LlmContextCollector.Components.Pages.HomePanels
{
    public partial class AzureDevOpsPanel
    {
        [Inject] private SettingsService SettingsService { get; set; } = null!;

        private List<string> _availableStatuses = new() { "New", "Active", "Resolved", "Closed", "Committed", "Proposed" };
        private List<string> _selectedStatuses = new() { "Active", "Resolved" };
        private List<string> _selectedAssignees = new() { "@Me" };
        private List<AdoIdentity> _projectMembers = new();
        private string _selectedType = "";
        private bool _isLoading = false;
        private bool _isLoadingMembers = false;
        private List<WorkItemSearchResult> _workItems = new();

        protected override async Task OnInitializedAsync()
        {
            await LoadMembers();
        }

        private async Task LoadMembers()
        {
            _isLoadingMembers = true;
            try
            {
                _projectMembers = await AdoService.GetProjectMembersAsync();
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
        private string _modalActiveTab = "edit";
        private string _editingState = string.Empty;
        private double? _editingRemainingWork;
        private double? _editingCompletedWork;
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
                    _modalActiveTab = "edit";
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
            int addedCount = 0;
            foreach (var file in pr.Files)
            {
                var cleanPath = file.Path.TrimStart('/');
                var node = AppState.FindNodeByPath(Path.Combine(AppState.ProjectRoot, cleanPath));

                if (node == null)
                {
                    var fileName = Path.GetFileName(cleanPath);
                    var flatNodes = new List<FileNode>();
                    Utils.FileTreeHelper.GetAllFileNodes(AppState.FileTree, flatNodes);
                    node = flatNodes.FirstOrDefault(n => n.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                }

                if (node != null)
                {
                    var relPath = Path.GetRelativePath(AppState.ProjectRoot, node.FullPath).Replace('\\', '/');
                    var originalPath = $"[ORIGINAL]{relPath}";

                    if (!AppState.SelectedFilesForContext.Contains(relPath))
                    {
                        AppState.SelectedFilesForContext.Add(relPath);
                        addedCount++;
                    }

                    bool isNew = file.ChangeType.Equals("add", StringComparison.OrdinalIgnoreCase);
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

        private async Task SaveAdoChanges()
        {
            _isSavingAdo = true;
            try
            {
                var fieldsSuccess = await AdoService.UpdateWorkItemFieldsAsync(_editingItemId, _editingState, _editingRemainingWork, _editingCompletedWork);
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
    }
}