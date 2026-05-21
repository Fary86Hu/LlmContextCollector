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

        private async Task SelectWorkItem(int id)
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
                LogService.LogError("ADO Panel", $"Hiba a betöltéskor (#{id})", ex.Message);
            }
            finally
            {
                AppState.HideLoading();
            }
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