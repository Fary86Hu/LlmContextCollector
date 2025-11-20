using LlmContextCollector.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LlmContextCollector.Services
{
    public class FileContextService
    {
        private readonly AppState _appState;
        private readonly ReferenceFinderService _referenceFinder;

        public FileContextService(AppState appState, ReferenceFinderService referenceFinder)
        {
            _appState = appState;
            _referenceFinder = referenceFinder;
        }

        public async Task AddSelectedTreeNodesToContextAsync()
        {
            var selectedNodes = new List<FileNode>();
            FindSelectedNodes(_appState.FileTree, selectedNodes);

            if (!selectedNodes.Any())
            {
                _appState.StatusText = "Nincs elem kiválasztva a fában a hozzáadáshoz.";
                return;
            }

            bool searchReferences = _appState.ReferenceSearchDepth > 0;
            bool searchReferencing = _appState.IncludeReferencingFiles;
            bool showLoading = searchReferences || searchReferencing;

            try
            {
                if (showLoading)
                {
                    _appState.ShowLoading("Fájlok hozzáadása és kapcsolatok keresése...");
                    await Task.Delay(1);
                }

                var projectRootPath = _appState.ProjectRoot ?? string.Empty;
                var filesFromSelection = new HashSet<string>();

                foreach (var node in selectedNodes)
                {
                    AddNodeAndChildrenToSet(node, projectRootPath, filesFromSelection);
                    node.IsSelectedInTree = false;
                }

                // Add companion .cs and .css files for .razor files
                var allProjectPaths = new HashSet<string>();
                GetAllFilePaths(_appState.FileTree, allProjectPaths, projectRootPath);

                var additionalFiles = new HashSet<string>();
                foreach (var selectedFile in filesFromSelection)
                {
                    if (selectedFile.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                    {
                        var csFile = selectedFile + ".cs";
                        if (allProjectPaths.Contains(csFile))
                        {
                            additionalFiles.Add(csFile);
                        }
                        var cssFile = selectedFile + ".css";
                        if (allProjectPaths.Contains(cssFile))
                        {
                            additionalFiles.Add(cssFile);
                        }
                    }
                }
                filesFromSelection.UnionWith(additionalFiles);

                var currentFiles = _appState.SelectedFilesForContext.ToHashSet();
                var initialCount = currentFiles.Count;
                currentFiles.UnionWith(filesFromSelection);
                
                var statusMessageParts = new List<string>();

                // 1. Forward Reference Search (Mit használ ez a fájl?)
                if (searchReferences && filesFromSelection.Any())
                {
                    var foundRefs = await _referenceFinder.FindReferencesAsync(filesFromSelection.ToList(), _appState.FileTree, projectRootPath, _appState.ReferenceSearchDepth);
                    var newRefsCount = foundRefs.Count(r => !currentFiles.Contains(r));
                    if (newRefsCount > 0) statusMessageParts.Add($"{newRefsCount} ref. fájl");
                    currentFiles.UnionWith(foundRefs);
                }

                // 2. Reverse Reference Search (Ki használja ezt a fájlt?)
                if (searchReferencing && filesFromSelection.Any())
                {
                    // A kereséshez azokat a fájlokat használjuk, amiket a user direktben kijelölt (plusz a companion fájlok),
                    // nem feltétlenül azokat, amiket az 1. lépésben találtunk (bár lehetne azokat is, de az exponenciális lehet).
                    // Most csak a közvetlen kijelölés hivatkozóit keressük.
                    var foundReferencing = await _referenceFinder.FindReferencingFilesAsync(filesFromSelection.ToList(), _appState.FileTree, projectRootPath);
                    var newReferencingCount = foundReferencing.Count(r => !currentFiles.Contains(r));
                    if (newReferencingCount > 0) statusMessageParts.Add($"{newReferencingCount} hivatkozó fájl");
                    currentFiles.UnionWith(foundReferencing);
                }

                var addedCount = currentFiles.Count - initialCount;
                if (addedCount > 0)
                {
                    _appState.SelectedFilesForContext.Clear();
                    foreach (var file in currentFiles.OrderBy(f => f))
                    {
                        _appState.SelectedFilesForContext.Add(file);
                    }
                    _appState.SaveContextListState();

                    if (statusMessageParts.Any())
                    {
                        _appState.StatusText = $"{addedCount} új fájl hozzáadva ({string.Join(", ", statusMessageParts)}).";
                    }
                    else
                    {
                        _appState.StatusText = $"{addedCount} fájl hozzáadva a kontextushoz.";
                    }
                }
                else
                {
                    _appState.StatusText = "Nem lett új fájl hozzáadva (már a listán voltak).";
                }
            }
            finally
            {
                if (showLoading)
                {
                    _appState.HideLoading();
                }
            }
        }

        public void RemoveFileListSelectionFromContext(List<string> selectedInContextList)
        {
            if (!selectedInContextList.Any())
            {
                _appState.StatusText = "Nincs fájl kijelölve az eltávolításhoz.";
                return;
            }

            var removedCount = 0;
            foreach (var selectedFile in selectedInContextList)
            {
                if (_appState.SelectedFilesForContext.Remove(selectedFile))
                {
                    removedCount++;
                }
            }
            
            if (removedCount > 0)
            {
                _appState.SaveContextListState();
                _appState.StatusText = $"{removedCount} fájl eltávolítva a kontextusból.";
            }
        }

        private void AddNodeAndChildrenToSet(FileNode node, string root, HashSet<string> files)
        {
            if (node.IsDirectory && node.IsVisible)
            {
                foreach (var child in node.Children)
                {
                    AddNodeAndChildrenToSet(child, root, files);
                }
            }
            else if (node.IsVisible && !node.IsDirectory)
            {
                var relativePath = Path.GetRelativePath(root, node.FullPath).Replace('\\', '/');
                files.Add(relativePath);
            }
        }

        private void FindSelectedNodes(IEnumerable<FileNode> nodes, List<FileNode> selected)
        {
            foreach (var node in nodes)
            {
                if (node.IsSelectedInTree)
                {
                    selected.Add(node);
                }
                if (node.Children.Any())
                {
                    FindSelectedNodes(node.Children, selected);
                }
            }
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