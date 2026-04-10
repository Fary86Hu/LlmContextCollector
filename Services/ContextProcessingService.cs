using LlmContextCollector.Components.Pages.HomePanels;
using LlmContextCollector.Models;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace LlmContextCollector.Services
{
    public class ContextProcessingService
    {
        private readonly AppState _appState;
        private readonly PromptService _promptService;
        private readonly LlmResponseParserService _llmResponseParserService;
        private readonly GitService _gitService;
        private readonly GitWorkflowService _gitWorkflowService;
        private const string AdoFilePrefix = "[ADO]";
        private const string OriginalFilePrefix = "[ORIGINAL]";

        public ContextProcessingService(AppState appState, PromptService promptService, LlmResponseParserService llmResponseParserService, GitService gitService, GitWorkflowService gitWorkflowService)
        {
            _appState = appState;
            _promptService = promptService;
            _llmResponseParserService = llmResponseParserService;
            _gitService = gitService;
            _gitWorkflowService = gitWorkflowService;
        }

        public async Task<string> BuildContextForClipboardAsync(bool includePrompt, bool includeSystemPrompt, bool includeFiles, IEnumerable<string> sortedFilePaths)
        {
            var sb = new StringBuilder();
            if (includePrompt && !string.IsNullOrWhiteSpace(_appState.PromptText))
            {
                sb.AppendLine(_appState.PromptText);
            }

            if (includeSystemPrompt)
            {
                var sysPrompt = await _promptService.GetSystemPromptAsync();
                if (!string.IsNullOrEmpty(sysPrompt))
                {
                    sb.AppendLine("\n--- SYSTEM INSTRUCTIONS ---\n");
                    sb.AppendLine(sysPrompt);
                    sb.AppendLine("\n--- END SYSTEM INSTRUCTIONS ---\n");
                }
            }

            if (_appState.IncludeProjectTreeInCopy && !string.IsNullOrEmpty(_appState.ProjectRoot))
            {
                sb.AppendLine("\n--- PROJECT STRUCTURE ---");
                sb.AppendLine("// Note: [*] indicates files already included in the full context below.");
                AppendCompactTreeRecursive(_appState.FileTree, sb, 0);
                sb.AppendLine("--- END PROJECT STRUCTURE ---\n");
            }

            var selectedDocs = _appState.AttachableDocuments.Where(d => d.IsSelected).ToList();
            if (selectedDocs.Any())
            {
                sb.AppendLine("\n--- ATTACHED DOCUMENTS ---");
                foreach (var doc in selectedDocs)
                {
                    sb.AppendLine($"\n// --- Document: {doc.Title} ---");
                    sb.AppendLine(doc.Content);
                }
                sb.AppendLine("\n--- END ATTACHED DOCUMENTS ---\n");
            }

            if (includeFiles && _appState.SelectedFilesForContext.Any())
            {
                if (sb.Length > 0) sb.AppendLine("\n\n// --- Kód Kontextus alább --- \n");
                foreach (var fileRelPath in sortedFilePaths)
                {
                    string fullPath;
                    string header;

                    if (fileRelPath.StartsWith(AdoFilePrefix))
                    {
                        var fileName = fileRelPath.Substring(AdoFilePrefix.Length);
                        fullPath = Path.Combine(_appState.AdoDocsPath, fileName);
                        header = $"// --- Dokumentum: {fileName} ---";
                        if (File.Exists(fullPath))
                        {
                            sb.AppendLine(header);
                            sb.AppendLine(await File.ReadAllTextAsync(fullPath));
                            sb.AppendLine();
                        }
                    }
                    else if (fileRelPath.StartsWith(OriginalFilePrefix))
                    {
                        var purePath = fileRelPath.Substring(OriginalFilePrefix.Length);
                        var devBranch = await _gitWorkflowService.GetDevelopmentBranchNameAsync();
                        var originalContent = await _gitService.GetFileContentAtBranchAsync(devBranch, purePath);
                        header = $"// --- Fájl: {purePath} (EREDETI VERZIÓ a(z) {devBranch} ágról) ---";
                        sb.AppendLine(header);
                        sb.AppendLine(originalContent);
                        sb.AppendLine();
                    }
                    else if (!string.IsNullOrEmpty(_appState.ProjectRoot))
                    {
                        fullPath = Path.GetFullPath(Path.Combine(_appState.ProjectRoot, fileRelPath.Replace('/', Path.DirectorySeparatorChar)));
                        var fullRoot = Path.GetFullPath(_appState.ProjectRoot);
                        if (!fullPath.StartsWith(fullRoot)) continue;

                        header = $"// --- Fájl: {fileRelPath.Replace('\\', '/')} ---";
                        if (File.Exists(fullPath))
                        {
                            sb.AppendLine(header);
                            sb.AppendLine(await File.ReadAllTextAsync(fullPath));
                            sb.AppendLine();
                        }
                    }
                }
            }
            return sb.ToString().Trim();
        }

        public string BuildContextForBuildErrors(IEnumerable<BuildError> errors)
        {
            var sb = new StringBuilder();
            sb.AppendLine("A legutóbbi build során az alábbi hibák keletkeztek. Kérlek, elemezd őket és javítsd a kódot!");
            sb.AppendLine();

            foreach (var error in errors)
            {
                sb.AppendLine($"- Hiba: {error.ErrorCode}");
                sb.AppendLine($"  Fájl: {error.FilePath}");
                sb.AppendLine($"  Hely: {error.Line}. sor, {error.Column}. oszlop");
                sb.AppendLine($"  Üzenet: {error.Message}");
                sb.AppendLine();

                if (!_appState.SelectedFilesForContext.Contains(error.FilePath))
                {
                    _appState.SelectedFilesForContext.Add(error.FilePath);
                }
            }

            _appState.SaveContextListState();

            sb.AppendLine("A fenti hibák javításához szükséges fájlokat hozzáadtam a kontextushoz. Kérlek, fókuszálj a hibaüzenetekben megjelölt sorokra.");
            return sb.ToString();
        }

        private void AppendCompactTreeRecursive(IEnumerable<FileNode> nodes, StringBuilder sb, int indent)
        {
            if (nodes == null || !nodes.Any()) return;

            var indentation = new string(' ', indent * 2);

            foreach (var node in nodes)
            {
                if (node.IsDirectory)
                {
                    sb.AppendLine($"{indentation}{node.Name}/");
                    AppendCompactTreeRecursive(node.Children, sb, indent + 1);
                }
                else
                {
                    var relPath = Path.GetRelativePath(_appState.ProjectRoot, node.FullPath).Replace('\\', '/');
                    var isIncluded = _appState.SelectedFilesForContext.Contains(relPath);

                    sb.AppendLine($"{indentation}{(isIncluded ? "[*] " : "")}{node.Name}");
                }
            }
        }

        public async Task<DiffResultArgs> ProcessChangesFromClipboardAsync(string clipboardText)
        {
            var (explanation, parsedFiles) = _llmResponseParserService.ParseResponse(clipboardText);

            var locRegex = new Regex(@"<data name=""[^""]+"" xml:space=""preserve"">[\s\S]*?</data>", RegexOptions.IgnoreCase);
            var locMatches = locRegex.Matches(clipboardText);
            var localizationFragment = string.Join("\n", locMatches.Select(m => m.Value));

            if (!parsedFiles.Any() && string.IsNullOrEmpty(localizationFragment))
                return new DiffResultArgs(explanation, new List<DiffResult>(), clipboardText, string.Empty);

            var diffResults = new List<DiffResult>();
            foreach (var fileData in parsedFiles)
            {
                var targetRelPath = fileData.Path;
                var sourceRelPath = fileData.OldPath ?? targetRelPath;

                var fullPath = Path.GetFullPath(Path.Combine(_appState.ProjectRoot, targetRelPath.Replace('/', Path.DirectorySeparatorChar)));
                var sourceFullPath = Path.GetFullPath(Path.Combine(_appState.ProjectRoot, sourceRelPath.Replace('/', Path.DirectorySeparatorChar)));
                var fullRoot = Path.GetFullPath(_appState.ProjectRoot);

                if (!fullPath.StartsWith(fullRoot) || !sourceFullPath.StartsWith(fullRoot))
                {
                    fileData.Explanation += $"\n[HIBA: Érvénytelen útvonal: {fileData.Path}]";
                    continue;
                }

                var status = fileData.Status;
                string oldContent = "";
                string finalNewContent = fileData.NewContent;

                bool patchFailed = false;
                string failedPatchContent = string.Empty;

                if (File.Exists(sourceFullPath))
                {
                    oldContent = await File.ReadAllTextAsync(sourceFullPath);
                    if ((status == DiffStatus.Modified || status == DiffStatus.Renamed) && finalNewContent.Contains("<<<<<<< SEARCH"))
                    {
                        try
                        {
                            finalNewContent = ApplyPatches(oldContent, finalNewContent);
                        }
                        catch (Exception ex)
                        {
                            fileData.Explanation += $"\n[HIBA: {ex.Message}]";
                            patchFailed = true;
                            failedPatchContent = finalNewContent;
                            finalNewContent = oldContent;
                        }
                    }
                }
                else if (status == DiffStatus.Modified || status == DiffStatus.Renamed)
                {
                    if (finalNewContent.Contains("<<<<<<< SEARCH"))
                    {
                        status = DiffStatus.Error;
                        patchFailed = true;
                        failedPatchContent = finalNewContent;
                        fileData.Explanation += $"\n[HIBA: A forrásfájl ({sourceRelPath}) nem található, a SEARCH/REPLACE nem alkalmazható.]";
                    }
                    else
                    {
                        status = DiffStatus.NewFromModified;
                    }
                }

                diffResults.Add(new DiffResult
                {
                    Path = targetRelPath,
                    OriginalPath = fileData.OldPath,
                    OldContent = oldContent,
                    NewContent = finalNewContent,
                    Status = status,
                    Explanation = fileData.Explanation,
                    PatchFailed = patchFailed,
                    FailedPatchContent = failedPatchContent
                });
            }
            return new DiffResultArgs(explanation, diffResults, clipboardText, originalPrompt: "", localizationData: localizationFragment);
        }

        private string ApplyPatches(string originalContent, string patchContent)
        {
            string result = originalContent.Replace("\r\n", "\n").Replace("\r", "\n");
            string normalizedPatch = patchContent.Replace("\r\n", "\n").Replace("\r", "\n");

            var blocks = Regex.Split(normalizedPatch, @"(?m)^<<<<<<< SEARCH\s*\n");

            for (int i = 1; i < blocks.Length; i++)
            {
                string block = blocks[i];

                var midMatch = Regex.Match(block, @"(?m)^\s*=======\s*\n");
                var endMatch = Regex.Match(block, @"(?m)^\s*>>>>>>> REPLACE\s*(\n|$)");

                if (!midMatch.Success || !endMatch.Success) continue;

                string searchBlock = block.Substring(0, midMatch.Index);
                string replaceBlock = block.Substring(midMatch.Index + midMatch.Length, endMatch.Index - (midMatch.Index + midMatch.Length));

                if (searchBlock.EndsWith("\n")) searchBlock = searchBlock.Substring(0, searchBlock.Length - 1);
                if (replaceBlock.EndsWith("\n")) replaceBlock = replaceBlock.Substring(0, replaceBlock.Length - 1);

                int index = FindRobustMatch(result, searchBlock);

                if (index == -1)
                {
                    throw new Exception($"SEARCH blokk nem található (#{i}). Ellenőrizze a fájl tartalmát és a behúzásokat.");
                }

                result = result.Remove(index, searchBlock.Length).Insert(index, replaceBlock);
            }

            return result;
        }

        private int FindRobustMatch(string content, string searchBlock)
        {
            int idx = content.IndexOf(searchBlock);
            if (idx != -1) return idx;

            var contentLines = content.Split('\n');
            var searchLines = searchBlock.Split('\n');

            if (searchLines.Length == 0) return -1;

            for (int i = 0; i <= contentLines.Length - searchLines.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < searchLines.Length; j++)
                {
                    if (contentLines[i + j].TrimEnd() != searchLines[j].TrimEnd())
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    int charPos = 0;
                    for (int k = 0; k < i; k++) charPos += contentLines[k].Length + 1;
                    return charPos;
                }
            }

            return -1;
        }
    }
}