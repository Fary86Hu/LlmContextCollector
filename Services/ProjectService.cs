using LlmContextCollector.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LlmContextCollector.Services
{
    public class ProjectService
    {
        private readonly AppState _appState;
        private readonly FileSystemService _fileSystemService;
        private readonly GitService _gitService;
        private readonly AzureDevOpsService _azureDevOpsService;
        private readonly EmbeddingIndexService _embeddingIndexService;

        public ProjectService(
            AppState appState, 
            FileSystemService fileSystemService, 
            GitService gitService,
            AzureDevOpsService azureDevOpsService,
            EmbeddingIndexService embeddingIndexService)
        {
            _appState = appState;
            _fileSystemService = fileSystemService;
            _gitService = gitService;
            _azureDevOpsService = azureDevOpsService;
            _embeddingIndexService = embeddingIndexService;
        }

        public async Task ReloadProjectAsync(bool preserveSelection)
        {
            if (string.IsNullOrWhiteSpace(_appState.ProjectRoot) || !Directory.Exists(_appState.ProjectRoot))
            {
                _appState.StatusText = "Érvénytelen vagy nem létező mappa.";
                return;
            }

            _embeddingIndexService.CancelIndexing();
            
            // Build log és státusz ürítése projektváltáskor
            _appState.BuildOutput = string.Empty;
            _appState.CurrentBuildErrors.Clear();
            _appState.CurrentBuildStatus = BuildStatus.Idle;

            var filesToPreserve = preserveSelection ? _appState.SelectedFilesForContext.ToList() : new List<string>();
            _appState.SelectedFilesForContext.Clear();
            _appState.ResetContextListHistory();
            _embeddingIndexService.ClearIndex();

            var tree = await _fileSystemService.ScanDirectoryAsync(_appState.ProjectRoot);
            _appState.SetFileTree(tree);

            var gitDir = Path.Combine(_appState.ProjectRoot, ".git");
            _appState.IsGitRepository = Directory.Exists(gitDir);
            if (_appState.IsGitRepository)
            {
                var (branchName, success, _) = await _gitService.GetCurrentBranchAsync();
                if (success) _appState.CurrentGitBranch = branchName;
            }
            else
            {
                _appState.CurrentGitBranch = string.Empty;
            }

            await _azureDevOpsService.LoadSettingsForCurrentProjectAsync();
            _azureDevOpsService.UpdateAdoPaths(_appState.ProjectRoot);
            await ScanLaunchSettingsAsync();
            
            var allFilePaths = new HashSet<string>();
            Utils.FileTreeHelper.GetAllFilePaths(_appState.FileTree, allFilePaths, _appState.ProjectRoot);

            _appState.SelectedFilesForContext.Clear();
            foreach (var file in filesToPreserve)
            {
                if (file.StartsWith("[ADO]") || allFilePaths.Contains(file))
                {
                    _appState.SelectedFilesForContext.Add(file);
                }
            }
            _appState.SaveContextListState();
            _appState.StatusText = $"Szkennelés befejezve. {allFilePaths.Count} fájl található a fa nézetben.";
        }

        private async Task ScanLaunchSettingsAsync()
        {
            _appState.LaunchProfiles = new List<string>();
            var profiles = new List<string>();
            try
            {
                string searchDir = _appState.ProjectRoot;

                // Megpróbáljuk kitalálni a specifikus projekt mappát a parancsból
                var cmd = _appState.DefaultBuildCommand + " " + _appState.DefaultRunCommand;
                var csprojMatch = Regex.Match(cmd, @"(?<path>[\w\.\/\\]+\.csproj)");
                if (csprojMatch.Success)
                {
                    var csprojPath = csprojMatch.Groups[1].Value;
                    var fullCsprojPath = Path.IsPathRooted(csprojPath) ? csprojPath : Path.Combine(_appState.ProjectRoot, csprojPath);
                    var potentialDir = Path.GetDirectoryName(fullCsprojPath);
                    if (!string.IsNullOrEmpty(potentialDir) && Directory.Exists(potentialDir)) 
                        searchDir = potentialDir;
                }

                // Ha nem találtunk mappát, a gyökérben is keresünk a Properties mappában
                var settingsFiles = Directory.GetFiles(searchDir, "launchSettings.json", SearchOption.AllDirectories)
                                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && 
                                                 !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                                     .ToList();

                foreach (var file in settingsFiles)
                {
                    var json = await File.ReadAllTextAsync(file);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("profiles", out var profilesElem))
                    {
                        foreach (var profile in profilesElem.EnumerateObject())
                        {
                            // Támogatott futtatási típusok: Standard Project, MAUI Msix, vagy Executable
                            if (profile.Value.TryGetProperty("commandName", out var cmdName))
                            {
                                var cmdValue = cmdName.GetString();
                                if (cmdValue == "Project" || cmdValue == "MsixPackage" || cmdValue == "Executable")
                                {
                                    if (!profiles.Contains(profile.Name))
                                        profiles.Add(profile.Name);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            _appState.LaunchProfiles = profiles.OrderBy(p => p).ToList();
            
            // Ha a betöltött SelectedLaunchProfile nem szerepel az új listában, reseteljük
            if (!string.IsNullOrEmpty(_appState.SelectedLaunchProfile) && !_appState.LaunchProfiles.Contains(_appState.SelectedLaunchProfile))
            {
                _appState.SelectedLaunchProfile = _appState.LaunchProfiles.FirstOrDefault() ?? string.Empty;
            }
            else if (string.IsNullOrEmpty(_appState.SelectedLaunchProfile))
            {
                _appState.SelectedLaunchProfile = _appState.LaunchProfiles.FirstOrDefault() ?? string.Empty;
            }
            
            _appState.NotifyStateChanged(nameof(AppState.LaunchProfiles));
            _appState.NotifyStateChanged(nameof(AppState.SelectedLaunchProfile));
        }
    }
}