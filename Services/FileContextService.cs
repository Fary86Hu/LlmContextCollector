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

            bool showLoading = _appState.ReferenceSearchDepth > 0;

            try
            {
                if (showLoading)
                {
                    _appState.ShowLoading("Fájlok hozzáadása és referenciák keresése...");
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

                if (_appState.ReferenceSearchDepth > 0 && filesFromSelection.Any())
                {
                    var foundRefs = await _referenceFinder.FindReferencesAsync(filesFromSelection.ToList(), _appState.FileTree, projectRootPath, _appState.ReferenceSearchDepth);
                    var newRefsCount = foundRefs.Count(r => !currentFiles.Contains(r));
                    if (newRefsCount > 0) _appState.StatusText = $"{newRefsCount} új kapcsolódó fájl hozzáadva referenciák alapján.";
                    currentFiles.UnionWith(foundRefs);
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
                    if (!_appState.StatusText.Contains("referenciák"))
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