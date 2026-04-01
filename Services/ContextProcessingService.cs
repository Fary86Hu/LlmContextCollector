using LlmContextCollector.Components.Pages.HomePanels;
using LlmContextCollector.Models;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
                sb.AppendLine("\n--- PROJECT STRUCTURE (VISIBLE FILES) ---");
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

        private void AppendCompactTreeRecursive(IEnumerable<FileNode> nodes, StringBuilder sb, int indent)
        {
            var visibleNodes = nodes.Where(n => n.IsVisible).ToList();
            if (!visibleNodes.Any()) return;

            var indentation = new string(' ', indent * 2);

            foreach (var node in visibleNodes)
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
            _appState.LastLlmGlobalExplanation = explanation;

            // Lokalizációs adatok kinyerése
            var locRegex = new System.Text.RegularExpressions.Regex(@"<data name=""[^""]+"" xml:space=""preserve"">[\s\S]*?</data>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var locMatches = locRegex.Matches(clipboardText);
            var localizationFragment = string.Join("\n", locMatches.Select(m => m.Value));

            if (!parsedFiles.Any() && string.IsNullOrEmpty(localizationFragment)) 
                return new DiffResultArgs(explanation, new List<DiffResult>(), clipboardText, string.Empty);

            var diffResults = new List<DiffResult>();
            foreach (var fileData in parsedFiles)
            {
                var fullPath = Path.GetFullPath(Path.Combine(_appState.ProjectRoot, fileData.Path.Replace('/', Path.DirectorySeparatorChar)));
                var fullRoot = Path.GetFullPath(_appState.ProjectRoot);
                
                if (!fullPath.StartsWith(fullRoot))
                {
                    fileData.Explanation += $"\n[HIBA: Érvénytelen útvonal (Path Traversal gyanú): {fileData.Path}]";
                    continue;
                }

                var status = fileData.Status;
                string oldContent = "";
                string finalNewContent = fileData.NewContent;

                bool patchFailed = false;
                string failedPatchContent = string.Empty;

                if (File.Exists(fullPath))
                {
                    oldContent = await File.ReadAllTextAsync(fullPath);
                    if (status == DiffStatus.Modified && finalNewContent.Contains("<<<<<<< SEARCH"))
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
                            finalNewContent = oldContent; // Keep old content so they can edit it
                        }
                    }
                }
                else if (status == DiffStatus.Modified) status = DiffStatus.NewFromModified;

                diffResults.Add(new DiffResult
                {
                    Path = fileData.Path,
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
            string result = originalContent.Replace("\r\n", "\n");
            string[] parts = patchContent.Split(new[] { "<<<<<<< SEARCH" }, StringSplitOptions.None);

            for (int i = 1; i < parts.Length; i++)
            {
                string block = parts[i];
                // Deklaráljuk explicit típussal, hogy elkerüljük a kétértelműséget
                string[] splitBlock = block.Split(new[] { "=======" }, StringSplitOptions.None);

                if (splitBlock.Length < 2) continue;

                string searchBlock = splitBlock[0].TrimStart('\r', '\n').TrimEnd('\r', '\n').Replace("\r\n", "\n");

                // Itt használjuk a splitBlock-ot
                string restOfBlock = string.Join("=======", splitBlock.Skip(1));

                string[] replaceSplit = restOfBlock.Split(new[] { ">>>>>>> REPLACE" }, StringSplitOptions.None);
                if (replaceSplit.Length < 1) continue;

                string replaceBlock = replaceSplit[0].TrimStart('\r', '\n').TrimEnd('\r', '\n').Replace("\r\n", "\n");

                int index = result.IndexOf(searchBlock);
                if (index == -1)
                {
                    string trimmedSearch = searchBlock.Trim();
                    index = result.IndexOf(trimmedSearch);
                    if (index == -1) throw new Exception($"SEARCH blokk nem található (#{i})");
                    result = result.Remove(index, trimmedSearch.Length).Insert(index, replaceBlock);
                }
                else
                {
                    result = result.Remove(index, searchBlock.Length).Insert(index, replaceBlock);
                }
            }
            return result;
        }
    }
}