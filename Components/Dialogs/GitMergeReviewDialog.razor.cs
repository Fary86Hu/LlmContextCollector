using LlmContextCollector.Models;
using LlmContextCollector.Services;
using Microsoft.AspNetCore.Components;
using System.Linq;
using System.Threading.Tasks;

namespace LlmContextCollector.Components.Dialogs
{
    public partial class GitMergeReviewDialog : ComponentBase
    {
        [Inject]
        private AppState AppState { get; set; } = null!;

        [Inject]
        private GitMergeService GitMergeService { get; set; } = null!;

        [Parameter]
        public bool IsVisible { get; set; }

        private MergeConflictResult? SelectedConflict { get; set; }

        protected override void OnParametersSet()
        {
            if (IsVisible && SelectedConflict == null && AppState.MergeConflicts.Any())
            {
                SelectedConflict = AppState.MergeConflicts.First();
            }
        }

        private void SelectConflict(MergeConflictResult conflict)
        {
            SelectedConflict = conflict;
        }

        private async Task AbortMerge()
        {
            await GitMergeService.AbortMergeAsync();
            SelectedConflict = null;
        }

        private async Task CompleteMerge()
        {
            await GitMergeService.CompleteMergeAsync();
            SelectedConflict = null;
        }

        private bool AllResolved()
        {
            return AppState.MergeConflicts.All(c => !string.IsNullOrEmpty(c.ResolvedContent));
        }
    }
}