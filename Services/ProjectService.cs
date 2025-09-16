using LlmContextCollector.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            
            _azureDevOpsService.UpdateAdoPaths(_appState.ProjectRoot);
            
            var allFilePaths = new HashSet<string>();
            GetAllFilePaths(_appState.FileTree, allFilePaths, _appState.ProjectRoot);

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

        private void GetAllFilePaths(IEnumerable<FileNode> nodes, HashSet<string> paths, string root)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory)
                {
                    GetAllFilePaths(node.Children, paths, root);
                }
                else
                {
                    paths.Add(Path.GetRelativePath(root, node.FullPath).Replace('\\', '/'));
                }
            }
        }
    }
}