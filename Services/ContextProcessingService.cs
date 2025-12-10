using LlmContextCollector.Models;
using System.Text;
using LlmContextCollector.Components.Pages.HomePanels;

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

        public async Task<string> BuildContextForClipboardAsync(bool includePrompt, bool includeSystemPrompt, IEnumerable<string> sortedFilePaths)
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

            if (_appState.SelectedFilesForContext.Any())
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine("\n\n// --- Kód Kontextus alább --- \n");
                }
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
                    else
                    {
                        continue;
                    }

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

            if (!parsedFiles.Any())
            {
                return new DiffResultArgs(explanation, new List<DiffResult>(), clipboardText);
            }

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
                        try
                        {
                            finalNewContent = ApplyPatches(oldContent, finalNewContent);
                        }
                        catch (Exception ex)
                        {
                            fileData.Explanation += $"\n[HIBA a patch alkalmazásakor: {ex.Message}]";
                        }
                    }
                }
                else if (status == DiffStatus.Modified)
                {
                    status = DiffStatus.NewFromModified;
                }

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
            originalContent = originalContent.Replace("\r\n", "\n");
            var result = originalContent;

            var parts = patchContent.Split(new[] { "<<<<<<< SEARCH" }, StringSplitOptions.None);

            for (int i = 1; i < parts.Length; i++)
            {
                var block = parts[i];
                var splitBlock = block.Split(new[] { "=======" }, StringSplitOptions.None);

                if (splitBlock.Length < 2) continue;

                var searchBlock = splitBlock[0].TrimEnd('\r', '\n');
                if (searchBlock.StartsWith("\n")) searchBlock = searchBlock.Substring(1);
                if (searchBlock.StartsWith("\r\n")) searchBlock = searchBlock.Substring(2);

                var rest = string.Join("=======", splitBlock.Skip(1));
                var replaceSplit = rest.Split(new[] { ">>>>>>> REPLACE" }, StringSplitOptions.None);

                var replaceBlock = replaceSplit[0];
                if (replaceBlock.StartsWith("\n")) replaceBlock = replaceBlock.Substring(1);
                else if (replaceBlock.StartsWith("\r\n")) replaceBlock = replaceBlock.Substring(2);

                replaceBlock = replaceBlock.TrimEnd('\r', '\n');

                searchBlock = searchBlock.Replace("\r\n", "\n");
                replaceBlock = replaceBlock.Replace("\r\n", "\n");

                int index = result.IndexOf(searchBlock);
                if (index == -1)
                {
                    var trimmedSearch = searchBlock.Trim();
                    index = result.IndexOf(trimmedSearch);

                    if (index == -1)
                    {
                        throw new Exception($"Nem található a SEARCH blokk az eredeti fájlban (Block #{i}). Ellenőrizd a kontextust.");
                    }
                    else
                    {
                        result = result.Remove(index, trimmedSearch.Length).Insert(index, replaceBlock.TrimEnd());
                    }
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