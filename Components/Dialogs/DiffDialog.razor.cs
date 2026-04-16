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
    public partial class DiffDialog : ComponentBase, IDisposable
    {
        [Inject] private AcceptedResponseHistoryService AcceptedResponseHistoryService { get; set; } = null!;
        [Inject] private ContextProcessingService ContextProcessingService { get; set; } = null!;

        [Parameter] public bool IsVisible { get; set; }
        [Parameter] public string GlobalExplanation { get; set; } = string.Empty;
        [Parameter] public string FullLlmResponse { get; set; } = string.Empty;
        [Parameter] public string OriginalPrompt { get; set; } = string.Empty;
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
        private enum ViewMode { Uncommitted, SinceBranchCreation, AgainstBranch, LlmHistory }
        private ViewMode _selectedViewMode = ViewMode.Uncommitted;
        private List<string> _allBranches = new();
        private List<LlmHistoryEntry> _historyEntries = new();
        private Guid? _selectedHistoryEntryId;
        private string? _selectedTargetBranch;
        private bool _isLoadingDiffs = false;
        private bool _isRefreshingSuggestions = false;
        private bool _prevIsVisible = false;
        private bool _showContextMenu = false;
        private double _contextMenuX, _contextMenuY;
        private DiffUtility.DiffLineItem? _contextMenuTargetLine;
        private double _leftPaneWidthPercent = 35.0;
        private bool _isResizingPane = false;
        private bool _includeOriginalPromptInSuggestions = true;
        private double _windowWidth = 0;
        private CancellationTokenSource? _diffCts;
        
        private bool _isManualMergeMode = false;
        private string _manualMergeNewContent = string.Empty;
        private string _mergeSearchTerm = string.Empty;
        private List<string> _failedPatchLines = new();
        private int _currentFailedLineIndex = -1;
        private MarkupString _highlightedFailedPatch;

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
                    _isManualMergeMode = false;
                    _manualMergeNewContent = string.Empty;
                    _selectedViewMode = ViewMode.Uncommitted;
                    _historyEntries.Clear();
                    _selectedHistoryEntryId = null;
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
        private async Task ChangeViewMode(ViewMode mode)
        {
            _selectedViewMode = mode;
            if (mode == ViewMode.LlmHistory)
            {
                _historyEntries = await AcceptedResponseHistoryService.GetHistoryAsync(AppState.ProjectRoot);
                _localDiffResults.Clear();
                await SelectResult(null);
            }
        }

        private string GetSnippet(string explanation)
        {
            if (string.IsNullOrWhiteSpace(explanation)) return "Nincs magyarázat";
            var clean = explanation.Replace("\n", " ").Replace("\r", "");
            return clean.Length > 40 ? clean.Substring(0, 40) + "..." : clean;
        }

        private async Task OnHistoryEntrySelected(ChangeEventArgs e)
        {
            if (Guid.TryParse(e.Value?.ToString(), out var id))
            {
                _selectedHistoryEntryId = id;
                var entry = _historyEntries.FirstOrDefault(x => x.Id == id);
                if (entry != null)
                {
                    _localDiffResults = entry.Files.Select(f => { f.IsSelectedForAccept = true; return f; }).ToList();
                    _globalExplanationText = entry.Explanation;
                    await SelectResult(_localDiffResults.FirstOrDefault());
                }
            }
            else
            {
                _selectedHistoryEntryId = null;
                _localDiffResults.Clear();
                _globalExplanationText = string.Empty;
                await SelectResult(null);
            }
        }

        private async Task LoadSelectedDiffsAsync() 
        { 
            if (_selectedViewMode == ViewMode.LlmHistory) return;
            _isLoadingDiffs = true; 
            try 
            { 
                var gitMode = _selectedViewMode switch {
                    ViewMode.SinceBranchCreation => GitWorkflowService.DiffMode.SinceBranchCreation,
                    ViewMode.AgainstBranch => GitWorkflowService.DiffMode.AgainstBranch,
                    _ => GitWorkflowService.DiffMode.Uncommitted
                };
                _localDiffResults = await GitWorkflowService.GetDiffsAsync(gitMode, _selectedTargetBranch); 
                await SelectResult(_localDiffResults.FirstOrDefault()); 
            } 
            finally 
            { 
                _isLoadingDiffs = false; 
            } 
        }

        private async Task RefreshSuggestionsAsync() 
        { 
            _isRefreshingSuggestions = true; 
            AppState.StatusText = "Javaslatok generálása...";
            try 
            { 
                string? promptToUse = null;
                if (_includeOriginalPromptInSuggestions)
                {
                    promptToUse = !string.IsNullOrWhiteSpace(OriginalPrompt) ? OriginalPrompt : AppState.DiffOriginalPrompt;
                }

                var (b, c) = await GitSuggestionService.GetSuggestionsAsync(_localDiffResults, _globalExplanationText, promptToUse); 
                
                if (b == "suggestion-not-found")
                {
                    AppState.StatusText = "Hiba: A modell nem küldött érvényes javaslatot.";
                }
                else
                {
                    _suggestedBranch = b ?? ""; 
                    _suggestedCommit = c ?? ""; 
                    _hasSuggestions = !string.IsNullOrEmpty(b); 
                    AppState.StatusText = "Javaslatok frissítve.";
                }
            } 
            catch (Exception ex)
            {
                AppState.StatusText = $"AI Hiba: {ex.Message}";
                await JSRuntime.InvokeVoidAsync("alert", $"Nem sikerült a javaslat generálása: {ex.Message}");
            }
            finally 
            { 
                _isRefreshingSuggestions = false; 
            } 
        }
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

        private void OpenManualMerge(DiffResult result)
        {
            _selectedResult = result;
            _manualMergeNewContent = result.NewContent;
            _isManualMergeMode = true;
            _mergeSearchTerm = "";
            _currentFailedLineIndex = -1;

            // Kiszűrjük a SEARCH/REPLACE markereket a navigációhoz
            _failedPatchLines = result.FailedPatchContent
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !l.Contains("<<<<<<< SEARCH") && !l.Contains("=======") && !l.Contains(">>>>>>> REPLACE"))
                .ToList();
            
            UpdateMergeHighlighting();
        }

        private async Task NavigateFailedPatch(int direction)
        {
            if (!_failedPatchLines.Any()) return;

            _currentFailedLineIndex += direction;
            if (_currentFailedLineIndex < 0) _currentFailedLineIndex = _failedPatchLines.Count - 1;
            if (_currentFailedLineIndex >= _failedPatchLines.Count) _currentFailedLineIndex = 0;

            // Trimmeljük a kiválasztott sort (elejéről whitespace le)
            _mergeSearchTerm = _failedPatchLines[_currentFailedLineIndex].TrimStart();
            await UpdateMergeHighlighting();
        }

        private async Task UpdateMergeHighlighting()
        {
            if (_selectedResult == null) return;

            if (string.IsNullOrWhiteSpace(_mergeSearchTerm))
            {
                _highlightedFailedPatch = new MarkupString(System.Web.HttpUtility.HtmlEncode(_selectedResult.FailedPatchContent));
            }
            else
            {
                var encoded = System.Web.HttpUtility.HtmlEncode(_selectedResult.FailedPatchContent);
                var escapedSearch = System.Web.HttpUtility.HtmlEncode(_mergeSearchTerm);
                var highlighted = Regex.Replace(encoded, Regex.Escape(escapedSearch), m => $"<span class=\"highlight\">{m.Value}</span>", RegexOptions.IgnoreCase);
                _highlightedFailedPatch = new MarkupString(highlighted);

                // Jobb oldali textarea kijelölése és görgetése
                await JSRuntime.InvokeVoidAsync("selectTextInTextarea", "merge-editor-main", _mergeSearchTerm);
                // Bal oldali nézet görgetése a találathoz (ID alapján nem tudjuk, mert több lehet, de a konténer görgethető marad)
            }
        }

        private async Task SaveManualMerge()
        {
            if (_selectedResult != null)
            {
                _selectedResult.NewContent = _manualMergeNewContent;
                _selectedResult.PatchFailed = false;
                if (!_selectedResult.Explanation.Contains("[KÉZI BEOLVASZTÁS SIKERES]"))
                {
                    _selectedResult.Explanation += "\n[KÉZI BEOLVASZTÁS SIKERES]";
                }
                _isManualMergeMode = false;
                await OnContentChanged();
            }
        }

        private void CloseManualMerge()
        {
            _isManualMergeMode = false;
        }

        private async Task ProcessFixFromClipboardAsync()
        {
            var clipboardText = await Clipboard.GetTextAsync();
            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                AppState.StatusText = "A vágólap üres.";
                return;
            }

            try
            {
                var fixArgs = await ContextProcessingService.ProcessChangesFromClipboardAsync(clipboardText);
                if (!fixArgs.DiffResults.Any())
                {
                    AppState.StatusText = "Nem található feldolgozható módosítás a vágólapon.";
                    return;
                }

                int updatedCount = 0;
                int addedCount = 0;

                foreach (var fix in fixArgs.DiffResults)
                {
                    var existing = _localDiffResults.FirstOrDefault(r => r.Path.Equals(fix.Path, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.NewContent = fix.NewContent;
                        existing.PatchFailed = fix.PatchFailed;
                        existing.FailedPatchContent = fix.FailedPatchContent;
                        existing.Status = fix.Status;
                        existing.Explanation = (existing.Explanation + "\n[JAVÍTVA]: " + fix.Explanation).Trim();
                        existing.IsSelectedForAccept = true;
                        updatedCount++;
                    }
                    else
                    {
                        _localDiffResults.Add(fix);
                        addedCount++;
                    }
                }

                if (updatedCount > 0 || addedCount > 0)
                {
                    AppState.StatusText = $"Javítás alkalmazva: {updatedCount} fájl frissítve, {addedCount} új hozzáadva.";
                    if (_selectedResult != null)
                    {
                        var refreshed = _localDiffResults.FirstOrDefault(r => r.Path == _selectedResult.Path);
                        if (refreshed != null) await SelectResult(refreshed);
                    }
                }
            }
            catch (Exception ex)
            {
                AppState.StatusText = $"Hiba a javítás feldolgozásakor: {ex.Message}";
            }
        }

        private async Task CopyFixPromptToClipboard()
        {
            var failedFiles = _localDiffResults.Where(r => r.PatchFailed).ToList();
            if (!failedFiles.Any()) return;

            var sb = new StringBuilder();
            sb.AppendLine("A kapott válaszban az alábbi fájlok SEARCH/REPLACE blokkjait nem sikerült automatikusan feldolgozni, mert a SEARCH rész nem egyezik meg pontosan (karakterhelyesen, beleértve az indentációt is) a fájl aktuális tartalmával.");
            sb.AppendLine("Mellékeltem a fájlok aktuális helyi tartalmát, hogy ez alapján tudd pontosítani a SEARCH blokkokat.");
            sb.AppendLine();

            foreach (var file in failedFiles)
            {
                sb.AppendLine($"### Fájl: {file.Path}");
                sb.AppendLine("#### Az eredeti válaszodban küldött (hibás) blokk:");
                sb.AppendLine("```");
                sb.AppendLine(file.FailedPatchContent);
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("#### A fájl JELENLEGI pontos tartalma a lemezen:");
                sb.AppendLine("```");
                sb.AppendLine(file.OldContent);
                sb.AppendLine("```");
                sb.AppendLine("---");
                sb.AppendLine();
            }

            sb.AppendLine("Kérlek, vizsgáld meg a fájlok fentebb mellékelt aktuális tartalmát, és küldd el újra a módosításokat. Ügyelj rá, hogy:");
            sb.AppendLine("1. A SEARCH blokk tartalmának PONTOSAN (karakterre, szóközre, behúzásra egyezően) meg kell egyeznie a mellékelt JELENLEGI tartalommal.");
            sb.AppendLine("2. Ne hagyj ki sorokat a SEARCH blokkból a fájlban lévőhöz képest.");
            sb.AppendLine("3. Csak a javított fájlokat küldd vissza a standard Fájl: {útvonal} formátumban.");

            await Clipboard.SetTextAsync(sb.ToString());
            AppState.StatusText = "Bővített hibajavító prompt a vágólapra másolva.";
        }

        private async Task DeleteLine()
        {
            if (_selectedResult == null || _contextMenuTargetLine == null || _contextMenuTargetLine.NewIndex == null) return;

            double scrollPos = 0;
            try { scrollPos = await JSRuntime.InvokeAsync<double>("getScrollPosition", "diff-view-container"); } catch { }
            
            var lines = _selectedResult.NewContent.Replace("\r\n", "\n").Split('\n').ToList();
            int index = _contextMenuTargetLine.NewIndex.Value;
            
            if (index >= 0 && index < lines.Count)
            {
                lines.RemoveAt(index);
                _selectedResult.NewContent = string.Join("\n", lines);
                _showContextMenu = false;
                
                await OnContentChanged();

                await Task.Yield();
                try { await JSRuntime.InvokeVoidAsync("setScrollPosition", "diff-view-container", scrollPos); } catch { }
            }
        }

        private async Task AcceptSelectedChanges() { var acc = _localDiffResults.Where(r => r.IsSelectedForAccept).ToList(); if (acc.Any()) await OnAccept.InvokeAsync(acc); }
        private async Task CreateBranch() { if (!string.IsNullOrEmpty(_suggestedBranch)) await OnCreateBranch.InvokeAsync(_suggestedBranch); }
        private async Task Commit() { var acc = _localDiffResults.Where(r => r.IsSelectedForAccept).ToList(); if (acc.Any()) await OnCommit.InvokeAsync(new CommitAndPushArgs(_suggestedBranch, _suggestedCommit, acc)); }
        private async Task Push() => await OnPush.InvokeAsync(new CommitAndPushArgs(AppState.CurrentGitBranch, _suggestedCommit, _localDiffResults.Where(r => r.IsSelectedForAccept).ToList()));
        private string GetStatusText(DiffResult r) => r.Status switch
        {
            DiffStatus.AlreadyApplied => "MÁR SZEREPEL",
            DiffStatus.NewFromModified => "ÚJ (FORRÁSBÓL)",
            _ => r.Status.ToString().ToUpper()
        };
        private string GetStatusClass(DiffResult r) => r.Status.ToString().ToLower();
        private async Task Close() { await OnClose.InvokeAsync(); }
        private async Task RevertSelectedHistoryChanges()
        {
            var acc = _localDiffResults.Where(r => r.IsSelectedForAccept).ToList();
            if (acc.Any())
            {
                await GitWorkflowService.RevertLlmHistoryChangesAsync(acc);
                AppState.StatusText = $"{acc.Count} fájl sikeresen visszavonva az előzmények alapján.";
                await ChangeViewMode(ViewMode.Uncommitted);
                await LoadSelectedDiffsAsync();
            }
        }

        private async Task RevertEntireHistoryEntryAsync()
        {
            if (_selectedHistoryEntryId == null || !_historyEntries.Any()) return;

            var selectedIndex = _historyEntries.FindIndex(x => x.Id == _selectedHistoryEntryId);
            if (selectedIndex == -1) return;

            bool confirm = await JSRuntime.InvokeAsync<bool>("confirm", 
                $"Visszaállás a kiválasztott pontra: Ez a művelet visszavonja ezt a módosítást ÉS az összes azóta elfogadott LLM választ ({selectedIndex + 1} bejegyzés összesen). Folytatja?");
            
            if (!confirm) return;

            var finalReverts = new Dictionary<string, DiffResult>(StringComparer.OrdinalIgnoreCase);

            for (int i = selectedIndex; i >= 0; i--)
            {
                foreach (var file in _historyEntries[i].Files)
                {
                    if (!finalReverts.ContainsKey(file.Path))
                    {
                        finalReverts[file.Path] = file;
                    }
                }
            }

            if (finalReverts.Any())
            {
                await GitWorkflowService.RevertLlmHistoryChangesAsync(finalReverts.Values.ToList());
                AppState.StatusText = $"Visszaállítva a(z) {selectedIndex + 1} bejegyzéssel ezelőtti állapotra ({finalReverts.Count} fájl érintett).";
                await ChangeViewMode(ViewMode.Uncommitted);
                await LoadSelectedDiffsAsync();
            }
        }

        private async Task RevertFile(DiffResult r) 
        { 
            bool confirm = await JSRuntime.InvokeAsync<bool>("confirm", $"Biztosan visszaállítja a fájlt és eldobja a változtatásokat? ({r.Path})");
            if (!confirm) return;

            if (_isGitDiffMode) 
            {
                if (_selectedViewMode == ViewMode.LlmHistory)
                {
                    await GitWorkflowService.RevertLlmHistoryChangesAsync(new List<DiffResult> { r });
                    AppState.StatusText = $"Fájl módosítás visszavonva az előzmények alapján: {r.Path}";
                    r.IsSelectedForAccept = false;
                    // Itt nem töltünk újra, mert az előzmény lista statikus, csak megjelöljük
                }
                else
                {
                    // Meghatározzuk a bázist, amire vissza kell állni
                    string sourceRef = "HEAD";
                    if (_selectedViewMode == ViewMode.SinceBranchCreation)
                    {
                        sourceRef = await GitWorkflowService.GetDevelopmentBranchNameAsync();
                    }
                    else if (_selectedViewMode == ViewMode.AgainstBranch && !string.IsNullOrEmpty(_selectedTargetBranch))
                    {
                        sourceRef = _selectedTargetBranch;
                    }

                    await GitWorkflowService.DiscardFileChangesAsync(r, sourceRef); 
                    await LoadSelectedDiffsAsync(); 
                    AppState.StatusText = $"Fájl visszaállítva ({sourceRef}): {r.Path}";
                }
            }
            else 
            {
                // Clipboard módban a visszaállítás csak a javaslat elvetését jelenti (mivel még nincs a lemezen)
                _localDiffResults.Remove(r); 
                await SelectResult(_localDiffResults.FirstOrDefault());
                AppState.StatusText = $"Javasolt módosítás elvetve: {r.Path}";
            }
        }
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