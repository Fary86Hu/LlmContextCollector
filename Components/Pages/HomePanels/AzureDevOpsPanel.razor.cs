using LlmContextCollector.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace LlmContextCollector.Components.Pages.HomePanels
{
    public partial class AzureDevOpsPanel
    {
        private string _selectedStatus = "Active";
        private string _assignedTo = "@Me";
        private string _selectedType = "";
        private bool _isLoading = false;
        private List<WorkItemSearchResult> _workItems = new();

        private async Task LoadWorkItems()
        {
            _isLoading = true;
            try
            {
                _workItems = await AdoService.SearchWorkItemsAsync(_selectedStatus, _assignedTo, _selectedType);
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

        private async Task HandleKeyup(KeyboardEventArgs e)
        {
            if (e.Key == "Enter") await LoadWorkItems();
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
                    foreach (var img in res.Images)
                    {
                        AppState.AttachedImages.Add(img);
                    }
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