using LlmContextCollector.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LlmContextCollector.Services
{
    public class FileTreeFilterService
    {
        private readonly AppState _appState;

        public FileTreeFilterService(AppState appState)
        {
            _appState = appState;
        }

        public async Task FilterFileTreeAsync()
        {
            var term = _appState.SearchTerm.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(term))
            {
                ClearFileTreeFilter();
                return;
            }

            _appState.ShowLoading($"Szűrés: '{term}'...");
            await Task.Delay(1); 
            try
            {
                var matchedNodes = new HashSet<FileNode>();

                async Task SearchNodes(IEnumerable<FileNode> nodes)
                {
                    foreach (var node in nodes)
                    {
                        bool isPathMatch = node.Name.ToLowerInvariant().Contains(term);
                        bool isContentMatch = false;

                        if (isPathMatch)
                        {
                            node.IsPathMatch = true;
                        }

                        if (_appState.SearchInContent && !node.IsDirectory)
                        {
                            try
                            {
                                var content = await File.ReadAllTextAsync(node.FullPath);
                                if (content.ToLowerInvariant().Contains(term))
                                {
                                    isContentMatch = true;
                                    node.IsContentMatch = true;
                                }
                            }
                            catch { }
                        }

                        if (isPathMatch || isContentMatch)
                        {
                            matchedNodes.Add(node);
                            var current = node.Parent;
                            while (current != null)
                            {
                                matchedNodes.Add(current);
                                current = current.Parent;
                            }
                        }

                        if (node.IsDirectory)
                        {
                            await SearchNodes(node.Children);
                        }
                    }
                }

                await SearchNodes(_appState.FileTree);

                void UpdateVisibility(IEnumerable<FileNode> nodes)
                {
                    foreach (var node in nodes)
                    {
                        node.IsVisible = matchedNodes.Contains(node);
                        if (node.IsVisible && node.IsDirectory && !string.IsNullOrWhiteSpace(_appState.SearchTerm))
                        {
                            node.IsExpanded = true;
                        }
                        if (node.IsDirectory)
                        {
                            UpdateVisibility(node.Children);
                        }
                    }
                }
                UpdateVisibility(_appState.FileTree);
                var fileMatchCount = matchedNodes.Count(n => !n.IsDirectory);
                _appState.StatusText = $"{fileMatchCount} fájl található a szűrésben.";
            }
            finally
            {
                _appState.HideLoading();
            }
        }

        public void ClearFileTreeFilter()
        {
            void UpdateVisibility(IEnumerable<FileNode> nodes)
            {
                foreach (var node in nodes)
                {
                    node.IsVisible = true;
                    node.IsExpanded = false;
                    node.IsContentMatch = false;
                    node.IsPathMatch = false;
                    if (node.IsDirectory)
                    {
                        UpdateVisibility(node.Children);
                    }
                }
            }
            UpdateVisibility(_appState.FileTree);
            _appState.StatusText = "Szűrés törölve, fa nézet visszaállítva.";
        }
    }
}