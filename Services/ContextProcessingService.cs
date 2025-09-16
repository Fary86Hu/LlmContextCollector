using LlmContextCollector.Models;
using LlmContextCollector.Services;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using LlmContextCollector.Components.Pages.HomePanels;
using System.Linq;

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

        public async Task<string> BuildContextForClipboardAsync(bool includePrompt, bool includeGlobalPrefix, IEnumerable<string> sortedFilePaths)
        {
            var sb = new StringBuilder();
            if (includePrompt && !string.IsNullOrWhiteSpace(_appState.PromptText))
            {
                sb.AppendLine(_appState.PromptText);
            }

            if (includeGlobalPrefix)
            {
                var globalPrefix = await _promptService.GetGlobalPrefixAsync();
                if (!string.IsNullOrEmpty(globalPrefix))
                {
                    sb.AppendLine(globalPrefix);
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
                return new DiffResultArgs(explanation, new List<DiffResult>());
            }

            var diffResults = new List<DiffResult>();
            foreach (var fileData in parsedFiles)
            {
                var fullPath = Path.Combine(_appState.ProjectRoot, fileData.Path.Replace('/', Path.DirectorySeparatorChar));
                var status = fileData.Status;
                string oldContent = "";

                if (status == DiffStatus.Modified)
                {
                    if (File.Exists(fullPath))
                    {
                        oldContent = await File.ReadAllTextAsync(fullPath);
                    }
                    else
                    {
                        status = DiffStatus.NewFromModified;
                    }
                }

                diffResults.Add(new DiffResult
                {
                    Path = fileData.Path,
                    OldContent = oldContent,
                    NewContent = fileData.NewContent,
                    Status = status
                });
            }

            return new DiffResultArgs(explanation, diffResults);
        }
    }
}