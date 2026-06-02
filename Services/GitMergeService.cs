using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LlmContextCollector.AI;
using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class GitMergeService
    {
        private readonly GitService _gitService;
        private readonly AiProviderFactory _providerFactory;
        private readonly AppState _appState;
        private readonly AppLogService _logService;

        public GitMergeService(GitService gitService, AiProviderFactory providerFactory, AppState appState, AppLogService logService)
        {
            _gitService = gitService;
            _providerFactory = providerFactory;
            _appState = appState;
            _logService = logService;
        }

        public async Task<bool> StartMergeAsync(string targetBranch)
        {
            _logService.LogInfo("GitMerge", $"Beolvasztás indítása: {targetBranch} -> {_appState.CurrentGitBranch}");
            _appState.MergeTargetBranch = targetBranch;
            _appState.MergeConflicts.Clear();

            var (success, output, error) = await _gitService.RunGitCommandAsync(new[] { "merge", targetBranch });

            if (success)
            {
                _appState.StatusText = $"Sikeresen beolvasztva: {targetBranch}.";
                return true;
            }

            _logService.LogWarning("GitMerge", "Konfliktus észlelt beolvasztás közben.");

            var (statusSuccess, statusOutput, _) = await _gitService.RunGitCommandAsync(new[] { "status", "--porcelain" });
            if (!statusSuccess) return false;

            var conflictedFiles = new List<string>();
            var lines = statusOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length > 3)
                {
                    var statusCode = line.Substring(0, 2);
                    var filePath = line.Substring(3).Trim();
                    if (statusCode == "UU" || statusCode == "AA" || statusCode == "DD" || statusCode == "AU" || statusCode == "UA")
                    {
                        conflictedFiles.Add(filePath);
                    }
                }
            }

            if (!conflictedFiles.Any())
            {
                _appState.StatusText = "A merge meghiúsult, de nem találtunk konfliktusos fájlokat.";
                return false;
            }

            _appState.ShowLoading($"Konfliktusok feloldása AI segítségével ({conflictedFiles.Count} fájl)...");
            var conflictsList = new List<MergeConflictResult>();

            foreach (var file in conflictedFiles)
            {
                var fullPath = Path.Combine(_appState.ProjectRoot, file);
                if (!File.Exists(fullPath)) continue;

                var contentWithMarkers = await File.ReadAllTextAsync(fullPath);
                var (ours, theirs) = ReconstructFullFiles(contentWithMarkers);

                var resolved = await ResolveConflictWithAiAsync(file, contentWithMarkers);

                conflictsList.Add(new MergeConflictResult
                {
                    Path = file,
                    OldContent = contentWithMarkers,
                    OursContent = ours,
                    TheirsContent = theirs,
                    ResolvedContent = resolved,
                    IsResolved = !string.IsNullOrEmpty(resolved)
                });
            }

            _appState.MergeConflicts = conflictsList;
            _appState.IsMergeDialogVisible = true;
            _appState.HideLoading();
            _appState.StatusText = $"Konfliktusok feloldva az AI által. Kérjük, ellenőrizze őket!";
            return false;
        }

        public async Task AbortMergeAsync()
        {
            _logService.LogInfo("GitMerge", "Beolvasztás félbeszakítása.");
            await _gitService.RunGitCommandAsync(new[] { "merge", "--abort" });
            _appState.MergeConflicts.Clear();
            _appState.IsMergeDialogVisible = false;
            _appState.StatusText = "Beolvasztás visszavonva.";
        }

        public async Task CompleteMergeAsync()
        {
            _logService.LogInfo("GitMerge", "Változtatások véglegesítése.");
            foreach (var conflict in _appState.MergeConflicts)
            {
                var fullPath = Path.Combine(_appState.ProjectRoot, conflict.Path);
                await File.WriteAllTextAsync(fullPath, conflict.ResolvedContent);
                await _gitService.RunGitCommandAsync(new[] { "add", conflict.Path });
            }

            var (success, _, error) = await _gitService.RunGitCommandAsync(new[] { "commit", "-m", $"Merge branch '{_appState.MergeTargetBranch}' using AI conflict resolution" });
            if (success)
            {
                _appState.MergeConflicts.Clear();
                _appState.IsMergeDialogVisible = false;
                _appState.StatusText = "Beolvasztás sikeresen véglegesítve!";
            }
            else
            {
                _appState.StatusText = $"Hiba a véglegesítés során: {error}";
            }
        }

        private (string ours, string theirs) ReconstructFullFiles(string content)
        {
            var oursBuilder = new StringBuilder();
            var theirsBuilder = new StringBuilder();

            var lines = content.Replace("\r\n", "\n").Split('\n');
            bool inOurs = false;
            bool inTheirs = false;

            foreach (var line in lines)
            {
                if (line.StartsWith("<<<<<<<"))
                {
                    inOurs = true;
                    inTheirs = false;
                }
                else if (line.StartsWith("======="))
                {
                    inOurs = false;
                    inTheirs = true;
                }
                else if (line.StartsWith(">>>>>>>"))
                {
                    inOurs = false;
                    inTheirs = false;
                }
                else
                {
                    if (inOurs)
                    {
                        oursBuilder.AppendLine(line);
                    }
                    else if (inTheirs)
                    {
                        theirsBuilder.AppendLine(line);
                    }
                    else
                    {
                        oursBuilder.AppendLine(line);
                        theirsBuilder.AppendLine(line);
                    }
                }
            }

            return (oursBuilder.ToString().TrimEnd(), theirsBuilder.ToString().TrimEnd());
        }

        private async Task<string> ResolveConflictWithAiAsync(string filePath, string fileContent)
        {
            try
            {
                var provider = _providerFactory.GetProvider(_appState.GitSuggestionModelId);
                var prompt = $"Te egy Szenior Szoftver Architect vagy. Az alábbi fájlban ({filePath}) git merge konfliktusok keletkeztek.\n" +
                             "A feladatod, hogy a konfliktusjelölők (<<<<<<< HEAD, =======, >>>>>>>) alapján olvaszd össze a két verziót értelmesen és helyesen.\n" +
                             "A kimenetedben KIZÁRÓLAG a teljesen feloldott, tiszta fájltartalmat add vissza, konfliktusjelölők nélkül. Ne írj semmilyen magyarázatot, bevezetést vagy markdown kódblokkot (pl. ```csharp).\n\n" +
                             "A KONFLIKTUSOS FÁJL TARTALMA:\n" +
                             fileContent;

                var result = await provider.GenerateAsync(prompt);
                
                var clean = result.Trim();
                if (clean.StartsWith("```"))
                {
                    var lines = clean.Split('\n');
                    if (lines.Length > 2)
                    {
                        clean = string.Join("\n", lines.Skip(1).Take(lines.Length - 2));
                    }
                }
                return clean.Trim();
            }
            catch (Exception ex)
            {
                _logService.LogError("GitMerge", $"Hiba a(z) {filePath} feloldása közben", ex.Message);
                return string.Empty;
            }
        }
    }
}