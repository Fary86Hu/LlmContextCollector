using LlmContextCollector.Models;
using LlmContextCollector.Utils;
using LlmContextCollector.Services;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace LlmContextCollector.Components.Dialogs
{
    public partial class DiffDialog : ComponentBase, IDisposable
    {
        [Parameter] public bool IsVisible { get; set; }
        [Parameter] public string GlobalExplanation { get; set; } = string.Empty;
        [Parameter] public string FullLlmResponse { get; set; } = string.Empty;
        [Parameter] public List<DiffResult>? DiffResults { get; set; }
        [Parameter] public EventCallback<List<DiffResult>> OnAccept { get; set; }
        [Parameter] public EventCallback OnClose { get; set; }
        [Parameter] public EventCallback<string> OnCreateBranch { get; set; }
        [Parameter] public EventCallback<CommitAndPushArgs> OnCommit { get; set; }
        [Parameter] public EventCallback<CommitAndPushArgs> OnPush { get; set; }

        private List<DiffResult> _localDiffResults = new();
        private DiffResult? _selectedResult;
        private List<DiffUtility.DiffLineItem> _unifiedDiffLines = new();
        private List<DiffMarkerInfo> _unifiedDiffMarkers = new();
        private string _suggestedBranch = string.Empty;
        private string _suggestedCommit = string.Empty;
        private string _globalExplanationText = string.Empty;
        private bool _hasSuggestions = false;
        private bool _isFullResponseView = false;
        private bool _isGitDiffMode = false;
        private bool _isGeneratingDiff = false;
        private GitWorkflowService.DiffMode _selectedDiffMode = GitWorkflowService.DiffMode.Uncommitted;
        private List<string> _allBranches = new();
        private string? _selectedTargetBranch;
        private bool _isLoadingDiffs = false;
        private bool _isRefreshingSuggestions = false;
        private bool _prevIsVisible = false;
        private bool _showContextMenu = false;
        private double _contextMenuX, _contextMenuY;
        private DiffUtility.DiffLineItem? _contextMenuTargetLine;
        private double _leftPaneWidthPercent = 35.0;
        private bool _isResizingPane = false;
        private double _windowWidth = 0;
        private CancellationTokenSource? _diffCts;

        private record DiffMarkerInfo(string Type, double TopPercent);

        protected override void OnInitialized() => AppState.PropertyChanged += OnAppStateChanged;

        protected override async Task OnParametersSetAsync()
        {
            bool becameVisible = IsVisible && !_prevIsVisible;
            bool dataChanged = IsVisible && DiffResults != null && (becameVisible || _localDiffResults.Count != DiffResults.Count);

            if (becameVisible || dataChanged)
            {
                if (becameVisible)
                {
                    _selectedResult = null;
                    _suggestedBranch = string.Empty;
                    _suggestedCommit = string.Empty;
                    _hasSuggestions = false;
                    _isFullResponseView = false;
                }

                _globalExplanationText = GlobalExplanation ?? string.Empty;
                _localDiffResults = DiffResults?.ToList() ?? new();
                _isGitDiffMode = string.IsNullOrEmpty(FullLlmResponse);
                
                if (_isGitDiffMode)
                {
                    _globalExplanationText = string.Empty;
                    if (becameVisible) await LoadBranchesAsync();
                }
                else
                {
                    ParseGlobalExplanation();
                }
                
                if (_selectedResult == null || !_localDiffResults.Any(r => r.Path == _selectedResult.Path))
                {
                    await SelectResult(_localDiffResults.FirstOrDefault());
                }
            }
            _prevIsVisible = IsVisible;
        }

        private async Task SelectResult(DiffResult? result)
        {
            _diffCts?.Cancel();
            _diffCts = new CancellationTokenSource();
            var token = _diffCts.Token;

            _selectedResult = result;
            
            try 
            {
                await GenerateDiffViewAsync(token);
            }
            catch (OperationCanceledException) { }
        }

        private async Task GenerateDiffViewAsync(CancellationToken ct)
        {
            _isGeneratingDiff = true;
            StateHasChanged();

            if (_selectedResult == null) 
            { 
                _unifiedDiffLines = new();
                _unifiedDiffMarkers = new();
                _isGeneratingDiff = false;
                return; 
            }

            var oldLines = _selectedResult.OldContent.Replace("\r\n", "\n").Split('\n');
            var newLines = _selectedResult.NewContent.Replace("\r\n", "\n").Split('\n');
            var opcodes = await DiffUtility.GetOpcodesAsync(oldLines, newLines);

            if (ct.IsCancellationRequested) return;

            var localUnifiedLines = new List<DiffUtility.DiffLineItem>();
            foreach (var op in opcodes)
            {
                switch (op.Tag)
                {
                    case 'e':
                        for (int i = 0; i < (op.I2 - op.I1); i++)
                            localUnifiedLines.Add(new DiffUtility.DiffLineItem(DiffUtility.DiffLineType.Context, oldLines[op.I1 + i], op.I1 + i, op.J1 + i));
                        break;
                    case 'd':
                        for (int i = 0; i < (op.I2 - op.I1); i++)
                            localUnifiedLines.Add(new DiffUtility.DiffLineItem(DiffUtility.DiffLineType.Delete, oldLines[op.I1 + i], op.I1 + i, null));
                        break;
                    case 'i':
                        for (int j = 0; j < (op.J2 - op.J1); j++)
                            localUnifiedLines.Add(new DiffUtility.DiffLineItem(DiffUtility.DiffLineType.Add, newLines[op.J1 + j], null, op.J1 + j));
                        break;
                    case 'r':
                        for (int i = 0; i < (op.I2 - op.I1); i++)
                            localUnifiedLines.Add(new DiffUtility.DiffLineItem(DiffUtility.DiffLineType.Delete, oldLines[op.I1 + i], op.I1 + i, null));
                        for (int j = 0; j < (op.J2 - op.J1); j++)
                            localUnifiedLines.Add(new DiffUtility.DiffLineItem(DiffUtility.DiffLineType.Add, newLines[op.J1 + j], null, op.J1 + j));
                        break;
                }
            }

            if (ct.IsCancellationRequested) return;

            var localUnifiedMarkers = new List<DiffMarkerInfo>();
            double totalUnifiedCount = localUnifiedLines.Count;
            if (totalUnifiedCount > 0)
            {
                double currentLine = 0;
                foreach (var op in opcodes)
                {
                    string? markerType = op.Tag switch { 'd' => "del", 'i' => "add", 'r' => "replace", _ => null };
                    if (markerType != null) localUnifiedMarkers.Add(new DiffMarkerInfo(markerType, (currentLine / totalUnifiedCount) * 100));
                    currentLine += op.Tag switch { 'e' => op.I2 - op.I1, 'd' => op.I2 - op.I1, 'i' => op.J2 - op.J1, 'r' => (op.I2 - op.I1) + (op.J2 - op.J1), _ => 0 };
                }
            }

            if (ct.IsCancellationRequested) return;

            _unifiedDiffLines = localUnifiedLines;
            _unifiedDiffMarkers = localUnifiedMarkers;

            _isGeneratingDiff = false;
            StateHasChanged();
        }

        private async Task OnContentChanged() 
        { 
            _diffCts?.Cancel();
            _diffCts = new CancellationTokenSource();
            await GenerateDiffViewAsync(_diffCts.Token); 
        }

        private async void OnAppStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(AppState.CurrentGitBranch)) await InvokeAsync(StateHasChanged); }
        private void ToggleFullResponseView() => _isFullResponseView = !_isFullResponseView;
        private async Task LoadBranchesAsync() { _allBranches = await GitWorkflowService.GetBranchesAsync(); if (_allBranches.Any()) _selectedTargetBranch = _allBranches.First(); }
        private async Task LoadSelectedDiffsAsync() { _isLoadingDiffs = true; try { _localDiffResults = await GitWorkflowService.GetDiffsAsync(_selectedDiffMode, _selectedTargetBranch); await SelectResult(_localDiffResults.FirstOrDefault()); } finally { _isLoadingDiffs = false; } }
        private async Task RefreshSuggestionsAsync() { _isRefreshingSuggestions = true; try { var (b, c) = await GitSuggestionService.GetSuggestionsAsync(_localDiffResults, _globalExplanationText); _suggestedBranch = b ?? ""; _suggestedCommit = c ?? ""; _hasSuggestions = b != null; } finally { _isRefreshingSuggestions = false; } }
        private void ParseGlobalExplanation()
        {
            if (string.IsNullOrEmpty(GlobalExplanation)) return;
            var bMatch = Regex.Match(GlobalExplanation, @"\[BRANCH_SUGGESTION\]([\s\S]*?)\[/BRANCH_SUGGESTION\]");
            var cMatch = Regex.Match(GlobalExplanation, @"\[COMMIT_SUGGESTION\]([\s\S]*?)\[/COMMIT_SUGGESTION\]");
            if (bMatch.Success && cMatch.Success)
            {
                _suggestedBranch = bMatch.Groups[1].Value.Trim();
                _suggestedCommit = cMatch.Groups[1].Value.Trim();
                _hasSuggestions = true;
                _globalExplanationText = Regex.Replace(_globalExplanationText, @"\[BRANCH_SUGGESTION\][\s\S]*?\[/BRANCH_SUGGESTION\]", "").Trim();
                _globalExplanationText = Regex.Replace(_globalExplanationText, @"\[COMMIT_SUGGESTION\][\s\S]*?\[/COMMIT_SUGGESTION\]", "").Trim();
            }
        }
        private async Task CopyToClipboard(string t) { if (!string.IsNullOrEmpty(t)) await Clipboard.SetTextAsync(t); }
        private string GetDiffLineClass(DiffUtility.DiffLineItem l) => l.Type == DiffUtility.DiffLineType.Add ? "add" : (l.Type == DiffUtility.DiffLineType.Delete ? "del" : "");
        private string GetDiffLineMarker(DiffUtility.DiffLineItem l) => l.Type == DiffUtility.DiffLineType.Add ? "+" : (l.Type == DiffUtility.DiffLineType.Delete ? "-" : " ");
        private void HideContextMenu() => _showContextMenu = false;
        private void ShowLineContextMenu(MouseEventArgs e, DiffUtility.DiffLineItem l) 
        { 
            if (l.Type != DiffUtility.DiffLineType.Add) return; 
            _contextMenuX = e.ClientX; 
            _contextMenuY = e.ClientY; 
            _contextMenuTargetLine = l; 
            _showContextMenu = true; 
        }

        private async Task DeleteLine()
        {
            if (_selectedResult == null || _contextMenuTargetLine == null || _contextMenuTargetLine.NewIndex == null) return;
            
            var lines = _selectedResult.NewContent.Replace("\r\n", "\n").Split('\n').ToList();
            int index = _contextMenuTargetLine.NewIndex.Value;
            
            if (index >= 0 && index < lines.Count)
            {
                lines.RemoveAt(index);
                _selectedResult.NewContent = string.Join("\n", lines);
                _showContextMenu = false;
                await OnContentChanged();
            }
        }

        private async Task AcceptSelectedChanges() { var acc = _localDiffResults.Where(r => r.IsSelectedForAccept).ToList(); if (acc.Any()) await OnAccept.InvokeAsync(acc); }
        private async Task CreateBranch() { if (!string.IsNullOrEmpty(_suggestedBranch)) await OnCreateBranch.InvokeAsync(_suggestedBranch); }
        private async Task Commit() { var acc = _localDiffResults.Where(r => r.IsSelectedForAccept).ToList(); if (acc.Any()) await OnCommit.InvokeAsync(new CommitAndPushArgs(_suggestedBranch, _suggestedCommit, acc)); }
        private async Task Push() => await OnPush.InvokeAsync(new CommitAndPushArgs(AppState.CurrentGitBranch, _suggestedCommit, _localDiffResults.Where(r => r.IsSelectedForAccept).ToList()));
        private string GetStatusText(DiffResult r) => r.Status.ToString().ToUpper();
        private string GetStatusClass(DiffResult r) => r.Status.ToString().ToLower();
        private async Task Close() { await OnClose.InvokeAsync(); }
        private async Task RevertFile(DiffResult r) { if (_isGitDiffMode) await GitWorkflowService.DiscardFileChangesAsync(r); else _localDiffResults.Remove(r); await LoadSelectedDiffsAsync(); }
        private async Task StartPaneResize(MouseEventArgs e) { _isResizingPane = true; _windowWidth = await JSRuntime.InvokeAsync<double>("eval", "window.innerWidth"); }
        private void StopPaneResize(MouseEventArgs e) { _isResizingPane = false; }
        private void OnMouseMove(MouseEventArgs e) { if (_isResizingPane) _leftPaneWidthPercent = Math.Clamp((e.ClientX / _windowWidth) * 100, 15, 85); }
        
        public void Dispose() 
        {
            AppState.PropertyChanged -= OnAppStateChanged;
            _diffCts?.Cancel();
            _diffCts?.Dispose();
        }
    }
}