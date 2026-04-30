using LlmContextCollector.Models;
using LlmContextCollector.Utils;
using LlmContextCollector.Services;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Text;

namespace LlmContextCollector.Components.Dialogs
{
    public partial class LlmResponseReview : ComponentBase, IDisposable
    {
        [Inject] private AcceptedResponseHistoryService AcceptedResponseHistoryService { get; set; } = null!;
        [Inject] private ContextProcessingService ContextProcessingService { get; set; } = null!;
        [Inject] private IClipboard Clipboard { get; set; } = null!;
        [Inject] private AppState AppState { get; set; } = null!;

        [Parameter] public bool IsVisible { get; set; }
        [Parameter] public string GlobalExplanation { get; set; } = string.Empty;
        [Parameter] public string FullLlmResponse { get; set; } = string.Empty;
        [Parameter] public string OriginalPrompt { get; set; } = string.Empty;
        [Parameter] public List<DiffResult>? DiffResults { get; set; }
        [Parameter] public EventCallback<List<DiffResult>> OnAccept { get; set; }
        [Parameter] public EventCallback OnClose { get; set; }

        private List<DiffResult> _localDiffResults = new();
        private DiffResult? _selectedResult;
        private List<DiffUtility.DiffLineItem> _unifiedDiffLines = new();
        private List<DiffMarkerInfo> _unifiedDiffMarkers = new();
        private List<LlmHistoryEntry> _historyEntries = new();
        private Dictionary<string, int> _fileHistoryPointers = new();
        private string _liveContentOnDisk = string.Empty;

        private string _globalExplanationText = string.Empty;
        private bool _isFullResponseView = false;
        private bool _isGeneratingDiff = false;
        private bool _prevIsVisible = false;
        private double _leftPaneWidthPercent = 35.0;
        private double _topPaneHeightPercent = 30.0;
        private bool _isResizingPane = false;
        private bool _isResizingTopPane = false;
        private double _windowWidth = 0;
        private double _windowHeight = 0;
        private CancellationTokenSource? _diffCts;

        private int _currentPatchBlockIndex = -1;
        private int _patchBlockCount = 0;
        private int _activeDiffHighlightIndex = -1;

        private bool _isManualEditMode = false;
        private bool _showContextMenu = false;
        private double _contextMenuX, _contextMenuY;
        private DiffUtility.DiffLineItem? _contextMenuTargetLine;

        private record DiffMarkerInfo(string Type, double TopPercent);

        protected override async Task OnParametersSetAsync()
        {
            if (IsVisible && !_prevIsVisible)
            {
                _localDiffResults = DiffResults?.ToList() ?? new();
                _historyEntries = await AcceptedResponseHistoryService.GetHistoryAsync(AppState.ProjectRoot);
                _globalExplanationText = GlobalExplanation;

                _fileHistoryPointers.Clear();
                foreach (var res in _localDiffResults)
                {
                    _fileHistoryPointers[res.Path] = -1;
                }

                ParseGlobalExplanation();
                await SelectResult(_localDiffResults.FirstOrDefault());
            }
            _prevIsVisible = IsVisible;
        }

        private void ParseGlobalExplanation()
        {
            if (string.IsNullOrEmpty(GlobalExplanation)) return;
            _globalExplanationText = Regex.Replace(GlobalExplanation, @"\[BRANCH_SUGGESTION\][\s\S]*?\[/BRANCH_SUGGESTION\]", "").Trim();
            _globalExplanationText = Regex.Replace(_globalExplanationText, @"\[COMMIT_SUGGESTION\][\s\S]*?\[/COMMIT_SUGGESTION\]", "").Trim();
        }

        private async Task SelectResult(DiffResult? result)
        {
            _diffCts?.Cancel();
            _diffCts = new CancellationTokenSource();
            _selectedResult = result;
            _currentPatchBlockIndex = -1;
            _activeDiffHighlightIndex = -1;
            _isManualEditMode = false;

            if (_selectedResult != null)
            {
                var fullPath = Path.Combine(AppState.ProjectRoot, _selectedResult.Path.Replace('/', Path.DirectorySeparatorChar));
                _liveContentOnDisk = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath) : "";

                if (!_fileHistoryPointers.ContainsKey(_selectedResult.Path))
                {
                    _fileHistoryPointers[_selectedResult.Path] = -1;
                }

                await SyncNewContentToHistoryPointer(_selectedResult);
            }

            CalculatePatchBlocks();
            try { await GenerateDiffViewAsync(_diffCts.Token); }
            catch (OperationCanceledException) { }
        }

        private async Task SyncNewContentToHistoryPointer(DiffResult result)
        {
            _fileHistoryPointers.TryGetValue(result.Path, out int idx);
            string baseContent;

            if (idx == -1)
            {
                baseContent = _liveContentOnDisk;
            }
            else
            {
                var historyForFile = _historyEntries.SelectMany(e => e.Files).Where(f => f.Path == result.Path).ToList();
                baseContent = idx < historyForFile.Count ? historyForFile[idx].OldContent : _liveContentOnDisk;
            }

            if (result.FailedPatchContent.Contains("<<<<<<< SEARCH"))
            {
                var summary = ContextProcessingService.ApplyPatches(baseContent, result.FailedPatchContent);
                result.NewContent = summary.UpdatedContent;
                result.PatchFailed = summary.BlockResults.Any(r => !r.Success);
            }
            else
            {
                result.NewContent = result.NewContent; // Megtartjuk az eredetit, ha nem patch alapú
            }
        }

        private void CalculatePatchBlocks()
        {
            _patchBlockCount = (_selectedResult != null && !string.IsNullOrEmpty(_selectedResult.FailedPatchContent)) ? Regex.Matches(_selectedResult.FailedPatchContent, @"<<<<<<< SEARCH").Count : 0;
        }

        private async Task NavigatePatchBlock(int direction)
        {
            if (_patchBlockCount == 0 || _selectedResult == null) return;
            _currentPatchBlockIndex += direction;
            if (_currentPatchBlockIndex < 0) _currentPatchBlockIndex = _patchBlockCount - 1;
            if (_currentPatchBlockIndex >= _patchBlockCount) _currentPatchBlockIndex = 0;

            var matches = Regex.Matches(_selectedResult.FailedPatchContent, @"<<<<<<< SEARCH\s*\n?([\s\S]*?)\s*\n?=======");
            if (_currentPatchBlockIndex < matches.Count)
            {
                var match = matches[_currentPatchBlockIndex];
                await JSRuntime.InvokeVoidAsync("selectTextInTextarea", "patch-block-textarea", match.Value);
                var lines = match.Groups[1].Value.Trim().Split('\n');
                if (lines.Length > 0)
                {
                    var firstLine = lines[0].Trim();
                    for (int i = 0; i < _unifiedDiffLines.Count; i++)
                    {
                        if (_unifiedDiffLines[i].Content.Trim().Contains(firstLine))
                        {
                            _activeDiffHighlightIndex = i;
                            await JSRuntime.InvokeVoidAsync("scrollToElementInContainer", "diff-view-container", $"diff-row-{i}");
                            break;
                        }
                    }
                }
            }
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

            // A bal oldal (Old) mindig a fájlrendszeri jelenlegi állapot (Live)
            var oldLines = _liveContentOnDisk.Replace("\r\n", "\n").Split('\n');
            // A jobb oldal (New) a navigált history állapot + az LLM változtatásai
            var newLines = _selectedResult.NewContent.Replace("\r\n", "\n").Split('\n');
            var opcodes = await DiffUtility.GetOpcodesAsync(oldLines, newLines);
            if (ct.IsCancellationRequested) return;

            var lines = new List<DiffUtility.DiffLineItem>();
            foreach (var op in opcodes)
            {
                if (op.Tag == 'e') for (int i = 0; i < (op.I2 - op.I1); i++) lines.Add(new DiffUtility.DiffLineItem(DiffUtility.DiffLineType.Context, oldLines[op.I1 + i], op.I1 + i, op.J1 + i));
                else if (op.Tag == 'd') for (int i = 0; i < (op.I2 - op.I1); i++) lines.Add(new DiffUtility.DiffLineItem(DiffUtility.DiffLineType.Delete, oldLines[op.I1 + i], op.I1 + i, null));
                else if (op.Tag == 'i') for (int j = 0; j < (op.J2 - op.J1); j++) lines.Add(new DiffUtility.DiffLineItem(DiffUtility.DiffLineType.Add, newLines[op.J1 + j], null, op.J1 + j));
            }
            _unifiedDiffLines = lines;
            _isGeneratingDiff = false;
            StateHasChanged();
        }

        private void ToggleFullResponseView() => _isFullResponseView = !_isFullResponseView;
        private string GetStatusClass(DiffResult r) => r.Status.ToString().ToLower();
        private string GetStatusText(DiffResult r) => r.Status.ToString().ToUpper();
        private string GetDiffLineClass(DiffUtility.DiffLineItem l) => l.Type == DiffUtility.DiffLineType.Add ? "add" : (l.Type == DiffUtility.DiffLineType.Delete ? "del" : "");
        private string GetDiffLineMarker(DiffUtility.DiffLineItem l) => l.Type == DiffUtility.DiffLineType.Add ? "+" : (l.Type == DiffUtility.DiffLineType.Delete ? "-" : " ");
        private void HideContextMenu() => _showContextMenu = false;
        private void ShowLineContextMenu(MouseEventArgs e, DiffUtility.DiffLineItem l) { if (l.Type == DiffUtility.DiffLineType.Add) { _contextMenuX = e.ClientX; _contextMenuY = e.ClientY; _contextMenuTargetLine = l; _showContextMenu = true; } }
        private async Task Close() => await OnClose.InvokeAsync();
        private async Task AcceptSelectedChanges() { var acc = _localDiffResults.Where(r => r.IsSelectedForAccept).ToList(); if (acc.Any()) await OnAccept.InvokeAsync(acc); }
        private void DiscardChange(DiffResult r) { _localDiffResults.Remove(r); if (_selectedResult == r) SelectResult(_localDiffResults.FirstOrDefault()); }
        private async Task CopyToClipboard(string t) => await Clipboard.SetTextAsync(t);

        private bool HasMoreHistory(DiffResult result, int direction)
        {
            var historyForFile = _historyEntries.SelectMany(e => e.Files).Where(f => f.Path == result.Path).ToList();
            if (!historyForFile.Any()) return false;
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

            await SyncNewContentToHistoryPointer(result);

            if (_selectedResult?.Path == result.Path)
            {
                await GenerateDiffViewAsync(CancellationToken.None);
            }
        }

        private async Task ToggleManualEdit()
        {
            _isManualEditMode = !_isManualEditMode;
            if (!_isManualEditMode && _selectedResult != null)
            {
                if (_selectedResult.PatchFailed)
                {
                    _selectedResult.PatchFailed = false;
                }
                await GenerateDiffViewAsync(CancellationToken.None);
            }
        }

        private async Task ProcessFixFromClipboardAsync()
        {
            var text = await Clipboard.GetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                var args = await ContextProcessingService.ProcessChangesFromClipboardAsync(text);
                foreach (var fix in args.DiffResults)
                {
                    var existing = _localDiffResults.FirstOrDefault(r => r.Path.Equals(fix.Path, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.NewContent = fix.NewContent;
                        existing.PatchFailed = fix.PatchFailed;
                        existing.FailedPatchContent = fix.FailedPatchContent;
                        existing.Status = fix.Status;
                    }
                    else _localDiffResults.Add(fix);
                }
                if (_selectedResult != null) await SelectResult(_localDiffResults.FirstOrDefault(r => r.Path == _selectedResult.Path));
            }
        }

        private async Task CopyFixPromptToClipboard()
        {
            var failed = _localDiffResults.Where(r => r.PatchFailed).ToList();
            if (!failed.Any()) return;
            var sb = new StringBuilder();
            foreach (var f in failed) sb.AppendLine($"### Fájl: {f.Path}\n#### Hibás blokk:\n```\n{f.FailedPatchContent}\n```\n#### Aktuális:\n```\n{f.OldContent}\n```\n");
            await Clipboard.SetTextAsync(sb.ToString());
        }

        private async Task DeleteLine()
        {
            if (_selectedResult == null || _contextMenuTargetLine?.NewIndex == null) return;
            var lines = _selectedResult.NewContent.Replace("\r\n", "\n").Split('\n').ToList();
            int idx = _contextMenuTargetLine.NewIndex.Value;
            if (idx >= 0 && idx < lines.Count)
            {
                lines.RemoveAt(idx);
                _selectedResult.NewContent = string.Join("\n", lines);
                _showContextMenu = false;
                await SelectResult(_selectedResult);
            }
        }

        private async Task StartPaneResize(MouseEventArgs e) { _isResizingPane = true; _windowWidth = await JSRuntime.InvokeAsync<double>("eval", "window.innerWidth"); }
        private async Task StartTopPaneResize(MouseEventArgs e) { _isResizingTopPane = true; _windowHeight = await JSRuntime.InvokeAsync<double>("eval", "window.innerHeight"); }
        private void StopPaneResize(MouseEventArgs e) { _isResizingPane = _isResizingTopPane = false; }
        private void OnMouseMove(MouseEventArgs e)
        {
            if (_isResizingPane) _leftPaneWidthPercent = Math.Clamp((e.ClientX / _windowWidth) * 100, 15, 85);
            if (_isResizingTopPane) _topPaneHeightPercent = Math.Clamp((e.ClientY / _windowHeight) * 100, 10, 80);
        }

        public void Dispose() { _diffCts?.Cancel(); _diffCts?.Dispose(); }
    }
}