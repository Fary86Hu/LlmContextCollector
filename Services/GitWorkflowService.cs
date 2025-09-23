using LlmContextCollector.Components.Pages.HomePanels;
using LlmContextCollector.Models;
using Microsoft.JSInterop;

namespace LlmContextCollector.Services
{
    public class GitWorkflowService
    {
        private readonly GitService _gitService;
        private readonly GitSuggestionService _suggestionService;
        private readonly AppState _appState;

        public GitWorkflowService(GitService gitService, GitSuggestionService suggestionService, AppState appState)
        {
            _gitService = gitService;
            _suggestionService = suggestionService;
            _appState = appState;
        }

        public async Task<DiffResultArgs> PrepareGitDiffForReviewAsync()
        {
            var diffResults = new List<DiffResult>();

            // Tracked changes
            var (trackedSuccess, trackedDiff, trackedError) = await _gitService.RunGitCommandAsync("diff --name-status HEAD --no-color");
            if (!trackedSuccess) throw new InvalidOperationException(trackedError);

            var trackedLines = trackedDiff.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in trackedLines)
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                var statusChar = parts[0][0];
                var path = parts[1];
                if (statusChar == 'R' && parts.Length > 2)
                {
                    path = parts[2];
                    statusChar = 'M';
                }

                var result = new DiffResult { Path = path.Replace('\\', '/') };
                var fullPath = Path.Combine(_appState.ProjectRoot, path);

                switch (statusChar)
                {
                    case 'M':
                        result.Status = DiffStatus.Modified;
                        result.OldContent = (await _gitService.RunGitCommandAsync($"show HEAD:\"{path}\"")).output;
                        if (File.Exists(fullPath)) result.NewContent = await File.ReadAllTextAsync(fullPath);
                        break;
                    case 'D':
                        result.Status = DiffStatus.Deleted;
                        result.OldContent = (await _gitService.RunGitCommandAsync($"show HEAD:\"{path}\"")).output;
                        result.NewContent = "";
                        break;
                    default:
                        continue;
                }
                diffResults.Add(result);
            }

            // Untracked files
            var (untrackedSuccess, untrackedFiles, untrackedError) = await _gitService.RunGitCommandAsync("ls-files --others --exclude-standard");
            if (!untrackedSuccess) throw new InvalidOperationException(untrackedError);

            var untrackedLines = untrackedFiles.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in untrackedLines)
            {
                var cleanPath = path.Replace('\\', '/').Trim();
                var result = new DiffResult { Path = cleanPath, Status = DiffStatus.New, OldContent = "" };
                var fullPath = Path.Combine(_appState.ProjectRoot, cleanPath);
                if (File.Exists(fullPath)) result.NewContent = await File.ReadAllTextAsync(fullPath);
                diffResults.Add(result);
            }

            if (!diffResults.Any())
            {
                _appState.StatusText = "Nincs változás a legutóbbi commit óta.";
                return new DiffResultArgs("Nincs változás.", new List<DiffResult>(), string.Empty);
            }

            _appState.ShowLoading("Javaslatok generálása...");
            var (branch, commit) = await _suggestionService.GetSuggestionsAsync(diffResults, _appState.LastLlmGlobalExplanation);

            string explanation;
            string fullLlmResponse;
            if (branch != null && commit != null)
            {
                explanation = $"[BRANCH_SUGGESTION]{branch}[/BRANCH_SUGGESTION]\n[COMMIT_SUGGESTION]{commit}[/COMMIT_SUGGESTION]";
                fullLlmResponse = explanation; // In this case, the explanation IS the full response.
                _appState.StatusText = $"{diffResults.Count} változott fájl betöltve a Git-ből.";
            }
            else
            {
                explanation = "Hiba: A javaslatok generálása nem sikerült. A nyelvi modell nem érhető el vagy hibát adott.";
                fullLlmResponse = explanation;
                _appState.StatusText = "Figyelem: LLM hiba, nincsenek Git javaslatok.";
            }

            return new DiffResultArgs(explanation, diffResults, fullLlmResponse);
        }

        public async Task<(int acceptedCount, int errorCount)> AcceptChangesAsync(List<DiffResult> acceptedResults)
        {
            int acceptedCount = 0;
            int errorCount = 0;
            foreach (var result in acceptedResults)
            {
                try
                {
                    var fullPath = Path.Combine(_appState.ProjectRoot, result.Path.Replace('/', Path.DirectorySeparatorChar));

                    if (result.Status == DiffStatus.Deleted)
                    {
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                        }
                    }
                    else
                    {
                        var dir = Path.GetDirectoryName(fullPath);
                        if (dir != null && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        await File.WriteAllTextAsync(fullPath, result.NewContent);
                    }
                    result.Status = DiffStatus.Accepted;
                    acceptedCount++;
                }
                catch
                {
                    result.Status = DiffStatus.Error;
                    errorCount++;
                }
            }
            return (acceptedCount, errorCount);
        }

        public async Task CreateAndCheckoutBranchAsync(string branchName)
        {
            await _gitService.CreateAndCheckoutBranchAsync(branchName);
            _appState.CurrentGitBranch = branchName;
            _appState.StatusText = $"Átváltva a(z) '{branchName}' branch-re.";
        }

        public async Task CommitChangesAsync(CommitAndPushArgs args)
        {
            // 1. Fájlok mentése
            await AcceptChangesAsync(args.AcceptedFiles);
            _appState.StatusText = "Fájlok mentve. Fájlok stage-elése...";
            await Task.Delay(1);

            // 2. Fájlok stage-elése
            var filePathsToStage = args.AcceptedFiles.Select(f => f.Path);
            await _gitService.StageFilesAsync(filePathsToStage);
            _appState.StatusText = "Fájlok stage-elve. Commit létrehozása...";
            await Task.Delay(1);

            // 3. Commit
            await _gitService.CommitAsync(args.CommitMessage);
            _appState.StatusText = "Commit sikeres. A változások push-olhatók.";
        }

        public async Task PushChangesAsync(string branchName)
        {
            await _gitService.PushAsync(branchName);
            _appState.StatusText = $"Sikeres push a(z) '{branchName}' branch-re!";
        }
    }
}