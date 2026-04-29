using LlmContextCollector.Models;
using LlmContextCollector.Utils;
using LlmContextCollector.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace LlmContextCollector.Components.Dialogs
{
    public partial class GitDiffReview : ComponentBase, IDisposable
    {
        [Inject] private AcceptedResponseHistoryService AcceptedResponseHistoryService { get; set; } = null!;
        [Inject] private GitWorkflowService GitWorkflowService { get; set; } = null!;
        [Inject] private GitSuggestionService GitSuggestionService { get; set; } = null!;
        [Inject] private IClipboard Clipboard { get; set; } = null!;
        [Inject] private AppState AppState { get; set; } = null!;
        [Inject] private IJSRuntime JSRuntime { get; set; } = null!;

        [Parameter] public bool IsVisible { get; set; }
        [Parameter] public List<DiffResult>? DiffResults { get; set; }
        [Parameter] public EventCallback OnClose { get; set; }
        [Parameter] public EventCallback<string> OnCreateBranch { get; set; }
        [Parameter] public EventCallback<CommitAndPushArgs> OnCommit { get; set; }
        [Parameter] public EventCallback<CommitAndPushArgs> OnPush { get; set; }

        private List<DiffResult> _localDiffResults = new();
        private DiffResult? _selectedResult;
        private List<DiffUtility.DiffLineItem> _unifiedDiffLines = new();
        private enum ViewMode { Uncommitted, SinceBranchCreation, AgainstBranch, LlmHistory }
        private ViewMode _selectedViewMode = ViewMode.Uncommitted;
        private List<string> _allBranches = new();
        private List<LlmHistoryEntry> _historyEntries = new();
        private Dictionary<string, int> _fileHistoryPointers = new();
        private string _suggestedBranch = string.Empty;
        private string _suggestedCommit = string.Empty;
        private string _selectedTargetBranch = string.Empty;
        private bool _isLoadingDiffs = false;
        private bool _isGeneratingDiff = false;
        private bool _prevIsVisible = false;
        private bool _isResizingPane = false;
        private bool _isRefreshingSuggestions = false;
        private double _leftPaneWidthPercent = 35.0;
        private double _windowWidth = 0;
        private CancellationTokenSource? _diffCts;

        protected override async Task OnParametersSetAsync()
        {
            if (IsVisible && !_prevIsVisible)
            {
                _localDiffResults = DiffResults?.ToList() ?? new();
                _historyEntries = await AcceptedResponseHistoryService.GetHistoryAsync(AppState.ProjectRoot);
                _allBranches = await GitWorkflowService.GetBranchesAsync();
                _selectedTargetBranch = _allBranches.FirstOrDefault() ?? "";
                await SelectResult(_localDiffResults.FirstOrDefault());
            }
            _prevIsVisible = IsVisible;
        }

        private async Task SelectResult(DiffResult? result)
        {
            _diffCts?.Cancel(); 
            _diffCts = new CancellationTokenSource();
            _selectedResult = result;
            if (result != null) await GenerateDiffViewAsync(_diffCts.Token);
            else _unifiedDiffLines.Clear();
        }

        private async Task GenerateDiffViewAsync(CancellationToken ct)
        {
            _isGeneratingDiff = true; StateHasChanged();
            var oldLines = _selectedResult!.OldContent.Replace("\r\n", "\n").Split('\n');
            var newLines = _selectedResult!.NewContent.Replace("\r\n", "\n").Split('\n');
            var opcodes = await DiffUtility.GetOpcodesAsync(oldLines, newLines);
            if (ct.IsCancellationRequested) return;

            var lines = new List<DiffUtility.DiffLineItem>();
            foreach (var op in opcodes)
            {
                if (op.Tag == 'e') for (int i = 0; i < (op.I2 - op.I1); i++) lines.Add(new DiffUtility.DiffLineItem(DiffUtility.DiffLineType.Context, oldLines[op.I1 + i], null, null));
                else if (op.Tag == 'd') for (int i = 0; i < (op.I2 - op.I1); i++) lines.Add(new DiffUtility.DiffLineItem(DiffUtility.DiffLineType.Delete, oldLines[op.I1 + i], null, null));
                else if (op.Tag == 'i') for (int j = 0; j < (op.J2 - op.J1); j++) lines.Add(new DiffUtility.DiffLineItem(DiffUtility.DiffLineType.Add, newLines[op.J1 + j], null, null));
            }
            _unifiedDiffLines = lines; 
            _isGeneratingDiff = false; 
            StateHasChanged();
        }

        private async Task RefreshSuggestionsAsync()
        {
            _isRefreshingSuggestions = true;
            try 
            {
                var (b, c) = await GitSuggestionService.GetSuggestionsAsync(_localDiffResults, null, AppState.DiffOriginalPrompt);
                if (b != "suggestion-not-found") 
                { 
                    _suggestedBranch = b ?? ""; 
                    _suggestedCommit = c ?? ""; 
                }
            } 
            finally { _isRefreshingSuggestions = false; }
        }

        private async Task ChangeViewMode(ViewMode mode)
        {
            _selectedViewMode = mode;
            if (mode == ViewMode.LlmHistory) _historyEntries = await AcceptedResponseHistoryService.GetHistoryAsync(AppState.ProjectRoot);
            _localDiffResults.Clear(); 
            await SelectResult(null);
        }

        private async Task LoadSelectedDiffsAsync()
        {
            _isLoadingDiffs = true;
            var gitMode = _selectedViewMode switch 
            { 
                ViewMode.SinceBranchCreation => GitWorkflowService.DiffMode.SinceBranchCreation, 
                ViewMode.AgainstBranch => GitWorkflowService.DiffMode.AgainstBranch, 
                _ => GitWorkflowService.DiffMode.Uncommitted 
            };
            _localDiffResults = await GitWorkflowService.GetDiffsAsync(gitMode, _selectedTargetBranch);
            await SelectResult(_localDiffResults.FirstOrDefault());
            _isLoadingDiffs = false;
        }

        private async Task OnHistoryEntrySelected(ChangeEventArgs e)
        {
            if (Guid.TryParse(e.Value?.ToString(), out var id))
            {
                var entry = _historyEntries.FirstOrDefault(x => x.Id == id);
                if (entry != null) { _localDiffResults = entry.Files; await SelectResult(_localDiffResults.FirstOrDefault()); }
            }
        }

        private bool HasMoreHistory(DiffResult result, int direction) 
        { 
            var historyForFile = _historyEntries.SelectMany(e => e.Files).Where(f => f.Path == result.Path).ToList(); 
            _fileHistoryPointers.TryGetValue(result.Path, out int currentIdx); 
            int nextIdx = currentIdx + direction; 
            return nextIdx >= -1 && nextIdx < historyForFile.Count; 
        }
        
        private string GetHistoryPointerText(DiffResult result) { _fileHistoryPointers.TryGetValue(result.Path, out int idx); return idx == -1 ? "LIVE" : $"H{idx + 1}"; }
        
        private async Task NavigateFileHistoryAsync(DiffResult result, int direction) 
        { 
            var historyForFile = _historyEntries.SelectMany(e => e.Files).Where(f => f.Path == result.Path).ToList(); 
            _fileHistoryPointers.TryGetValue(result.Path, out int currentIdx); 
            int nextIdx = currentIdx + direction; 
            if (nextIdx < -1 || nextIdx >= historyForFile.Count) return; 
            _fileHistoryPointers[result.Path] = nextIdx; 
            result.OldContent = nextIdx == -1 ? (File.Exists(Path.Combine(AppState.ProjectRoot, result.Path.Replace('/', Path.DirectorySeparatorChar))) ? await File.ReadAllTextAsync(Path.Combine(AppState.ProjectRoot, result.Path.Replace('/', Path.DirectorySeparatorChar))) : "") : historyForFile[nextIdx].OldContent; 
            if (_selectedResult?.Path == result.Path) await SelectResult(result); 
        }

        private async Task RevertFile(DiffResult r) { await GitWorkflowService.DiscardFileChangesAsync(r); await LoadSelectedDiffsAsync(); }
        private async Task RevertSelectedHistoryChanges() { var acc = _localDiffResults.Where(r => r.IsSelectedForAccept).ToList(); if (acc.Any()) { await GitWorkflowService.RevertLlmHistoryChangesAsync(acc); await ChangeViewMode(ViewMode.Uncommitted); } }
        private async Task CreateBranch() => await OnCreateBranch.InvokeAsync(_suggestedBranch);
        private async Task Commit() => await OnCommit.InvokeAsync(new CommitAndPushArgs(_suggestedBranch, _suggestedCommit, _localDiffResults.Where(r => r.IsSelectedForAccept).ToList()));
        private async Task Push() => await OnPush.InvokeAsync(new CommitAndPushArgs(AppState.CurrentGitBranch, _suggestedCommit, _localDiffResults.Where(r => r.IsSelectedForAccept).ToList()));
        private string GetDiffLineClass(DiffUtility.DiffLineItem l) => l.Type == DiffUtility.DiffLineType.Add ? "add" : (l.Type == DiffUtility.DiffLineType.Delete ? "del" : "");
        private string GetDiffLineMarker(DiffUtility.DiffLineItem l) => l.Type == DiffUtility.DiffLineType.Add ? "+" : (l.Type == DiffUtility.DiffLineType.Delete ? "-" : " ");
        private string GetSnippet(string s) => s.Length > 40 ? s.Substring(0, 40) + "..." : s;
        private async Task Close() => await OnClose.InvokeAsync();
        private async Task CopyToClipboard(string t) => await Clipboard.SetTextAsync(t);
        private async Task StartPaneResize(MouseEventArgs e) { _isResizingPane = true; _windowWidth = await JSRuntime.InvokeAsync<double>("eval", "window.innerWidth"); }
        private void StopPaneResize(MouseEventArgs e) => _isResizingPane = false;
        private void OnMouseMove(MouseEventArgs e) { if (_isResizingPane) _leftPaneWidthPercent = Math.Clamp((e.ClientX / _windowWidth) * 100, 15, 85); }
        public void Dispose() { _diffCts?.Cancel(); _diffCts?.Dispose(); }
    }
}