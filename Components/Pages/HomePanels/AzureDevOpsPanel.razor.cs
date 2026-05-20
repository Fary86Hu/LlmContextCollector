using LlmContextCollector.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace LlmContextCollector.Components.Pages.HomePanels
{
    public partial class AzureDevOpsPanel
    {
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

        private async Task LoadWorkItems()
        {
            _isLoading = true;
            try
            {
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
    }
}