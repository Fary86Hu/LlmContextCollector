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
        private const string AdoFilePrefix = "[ADO]";

        public ContextProcessingService(AppState appState, PromptService promptService, LlmResponseParserService llmResponseParserService)
        {
            _appState = appState;
            _promptService = promptService;
            _llmResponseParserService = llmResponseParserService;
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
                    }
                    else if (!string.IsNullOrEmpty(_appState.ProjectRoot))
                    {
                        fullPath = Path.Combine(_appState.ProjectRoot, fileRelPath.Replace('/', Path.DirectorySeparatorChar));
                        header = $"// --- Fájl: {fileRelPath.Replace('\\', '/')} ---";
                    }
                    else continue;

                    if (File.Exists(fullPath))
                    {
                        sb.AppendLine(header);
                        sb.AppendLine(await File.ReadAllTextAsync(fullPath));
                        sb.AppendLine();
                    }
                }
            }
            return sb.ToString().Trim();
        }

        public async Task<DiffResultArgs> ProcessChangesFromClipboardAsync(string clipboardText)
        {
            var (explanation, parsedFiles) = _llmResponseParserService.ParseResponse(clipboardText);
            _appState.LastLlmGlobalExplanation = explanation;

            if (!parsedFiles.Any()) return new DiffResultArgs(explanation, new List<DiffResult>(), clipboardText);

            var diffResults = new List<DiffResult>();
            foreach (var fileData in parsedFiles)
            {
                var fullPath = Path.Combine(_appState.ProjectRoot, fileData.Path.Replace('/', Path.DirectorySeparatorChar));
                var status = fileData.Status;
                string oldContent = "";
                string finalNewContent = fileData.NewContent;

                if (File.Exists(fullPath))
                {
                    oldContent = await File.ReadAllTextAsync(fullPath);
                    if (status == DiffStatus.Modified && finalNewContent.Contains("<<<<<<< SEARCH"))
                    {
                        try { finalNewContent = ApplyPatches(oldContent, finalNewContent); }
                        catch (Exception ex) { fileData.Explanation += $"\n[HIBA: {ex.Message}]"; }
                    }
                }
                else if (status == DiffStatus.Modified) status = DiffStatus.NewFromModified;

                diffResults.Add(new DiffResult
                {
                    Path = fileData.Path,
                    OldContent = oldContent,
                    NewContent = finalNewContent,
                    Status = status,
                    Explanation = fileData.Explanation
                });
            }
            return new DiffResultArgs(explanation, diffResults, clipboardText);
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