using LlmContextCollector.Components.Pages.HomePanels;
using LlmContextCollector.Models;
using Microsoft.JSInterop;
using System.Text.RegularExpressions;

namespace LlmContextCollector.Services
{
    public class GitWorkflowService
    {
        private readonly GitService _gitService;
        private readonly GitSuggestionService _suggestionService;
        private readonly AppState _appState;

        public enum DiffMode { Uncommitted, SinceBranchCreation, AgainstBranch }

        public GitWorkflowService(GitService gitService, GitSuggestionService suggestionService, AppState appState)
        {
            _gitService = gitService;
            _suggestionService = suggestionService;
            _appState = appState;
        }

        public async Task<DiffResultArgs> PrepareGitDiffForReviewAsync()
        {
            var diffResults = await GetDiffsAsync(DiffMode.Uncommitted);
            _appState.StatusText = $"{diffResults.Count} változott fájl betöltve a Git-ből.";
            return new DiffResultArgs(string.Empty, diffResults, string.Empty);
        }

        public async Task<List<string>> GetBranchesAsync()
        {
            var (success, output, error) = await _gitService.RunGitCommandAsync("branch");
            if (!success) return new List<string>();

            return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(line => line.Trim().Replace("* ", ""))
                         .Where(b => !b.Contains("->"))
                         .ToList();
        }

        private async Task<string> GetDevelopmentBranchNameAsync()
        {
            var allBranches = await GetBranchesAsync();

            var (success, remoteBranchesOutput, _) = await _gitService.RunGitCommandAsync("branch -r");
            if (success)
            {
                var remoteBranches = remoteBranchesOutput
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(b => b.Trim())
                    .Where(b => !b.Contains("->"))
                    .Select(b => b.StartsWith("origin/") ? b.Substring("origin/".Length) : b);
                allBranches.AddRange(remoteBranches);
            }

            var uniqueBranches = allBranches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (uniqueBranches.Any(b => b.Equals("develop", StringComparison.OrdinalIgnoreCase)))
            {
                return "develop";
            }
            if (uniqueBranches.Any(b => b.Equals("main", StringComparison.OrdinalIgnoreCase)))
            {
                return "main";
            }
            if (uniqueBranches.Any(b => b.Equals("master", StringComparison.OrdinalIgnoreCase)))
            {
                return "master";
            }

            var (headSuccess, headOutput, _) = await _gitService.RunGitCommandAsync("symbolic-ref refs/remotes/origin/HEAD");
            if (headSuccess && !string.IsNullOrWhiteSpace(headOutput))
            {
                var match = Regex.Match(headOutput, @"refs/remotes/origin/(.+)");
                if (match.Success) return match.Groups[1].Value.Trim();
            }

            return "main";
        }


        public async Task<List<DiffResult>> GetDiffsAsync(DiffMode mode, string? targetBranch = null)
        {
            if (mode == DiffMode.Uncommitted)
            {
                return await GetUncommittedDiffsAsync();
            }

            string diffCommand;
            switch (mode)
            {
                case DiffMode.SinceBranchCreation:
                    var devBranch = await GetDevelopmentBranchNameAsync();
                    diffCommand = $"diff --name-status {devBranch}...HEAD";
                    break;
                case DiffMode.AgainstBranch:
                    if (string.IsNullOrWhiteSpace(targetBranch)) return new List<DiffResult>();
                    diffCommand = $"diff --name-status {targetBranch}...HEAD";
                    break;
                default:
                    return new List<DiffResult>();
            }

            var (success, output, error) = await _gitService.RunGitCommandAsync(diffCommand);
            if (!success) throw new InvalidOperationException($"Git diff failed: {error}");

            return await ParseDiffNameStatusOutputAsync(output, diffCommand.Split(' ')[0]);
        }

        private async Task<List<DiffResult>> GetUncommittedDiffsAsync()
        {
            var diffResults = new List<DiffResult>();
            var (trackedSuccess, trackedDiff, trackedError) = await _gitService.RunGitCommandAsync("diff --name-status HEAD");
            if (!trackedSuccess) throw new InvalidOperationException(trackedError);

            diffResults.AddRange(await ParseDiffNameStatusOutputAsync(trackedDiff, "HEAD"));

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

            return diffResults;
        }

        private async Task<List<DiffResult>> ParseDiffNameStatusOutputAsync(string output, string oldContentRef)
        {
            var diffResults = new List<DiffResult>();
            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;

                var statusChar = parts[0][0];
                var oldPath = parts[1];
                var newPath = parts.Length > 2 ? parts[2] : oldPath;

                var result = new DiffResult { Path = newPath.Replace('\\', '/') };
                var fullPath = Path.Combine(_appState.ProjectRoot, newPath);

                switch (statusChar)
                {
                    case 'M': 
                    case 'R': 
                        result.Status = DiffStatus.Modified;
                        result.OldContent = (await _gitService.RunGitCommandAsync($"show {oldContentRef}:\"{oldPath}\"")).output;
                        if (File.Exists(fullPath)) result.NewContent = await File.ReadAllTextAsync(fullPath);
                        break;
                    case 'A': 
                        result.Status = DiffStatus.New;
                        result.OldContent = "";
                        if (File.Exists(fullPath)) result.NewContent = await File.ReadAllTextAsync(fullPath);
                        break;
                    case 'D': 
                        result.Status = DiffStatus.Deleted;
                        result.OldContent = (await _gitService.RunGitCommandAsync($"show {oldContentRef}:\"{oldPath}\"")).output;
                        result.NewContent = "";
                        break;
                    default:
                        continue;
                }
                diffResults.Add(result);
            }
            return diffResults;
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
            await AcceptChangesAsync(args.AcceptedFiles);
            _appState.StatusText = "Fájlok mentve. Fájlok stage-elése...";
            await Task.Delay(1);

            var filePathsToStage = args.AcceptedFiles.Select(f => f.Path);
            await _gitService.StageFilesAsync(filePathsToStage);
            _appState.StatusText = "Fájlok stage-elve. Commit létrehozása...";
            await Task.Delay(1);

            await _gitService.CommitAsync(args.CommitMessage);
            _appState.StatusText = "Commit sikeres. A változások push-olhatók.";
        }

        public async Task PushChangesAsync(string branchName)
        {
            await _gitService.PushAsync(branchName);
            _appState.StatusText = $"Sikeres push a(z) '{branchName}' branch-re!";
        }

        public async Task DiscardFileChangesAsync(DiffResult diffResult)
        {
            var fullPath = Path.Combine(_appState.ProjectRoot, diffResult.Path.Replace('/', Path.DirectorySeparatorChar));

            if (diffResult.Status == DiffStatus.New)
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            else
            {
                await _gitService.DiscardChangesAsync(diffResult.Path);
            }
        }
    }
}
