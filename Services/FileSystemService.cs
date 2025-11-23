using LlmContextCollector.Models;
using System.Text.RegularExpressions;

namespace LlmContextCollector.Services
{
    public class FileSystemService
    {
        private readonly AppState _appState;
        private List<string> _simpleNameIgnores = new();
        private List<string> _relativePathIgnores = new();
        private List<Regex> _wildcardRegexes = new();


        public FileSystemService(AppState appState)
        {
            _appState = appState;
        }

        public Task<List<FileNode>> ScanDirectoryAsync(string rootPath)
        {
            return Task.Run(() =>
            {
                BuildIgnoreList(rootPath);
                var rootNode = new FileNode
                {
                    Name = new DirectoryInfo(rootPath).Name,
                    FullPath = rootPath,
                    IsDirectory = true,
                    IsExpanded = true
                };

                var extensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                ScanDirectoryRecursively(rootNode, extensionCounts);

                // Update AppState statistics
                _appState.ExtensionCounts = extensionCounts;

                // Sync ExtensionFilters: Add new found extensions
                foreach (var ext in extensionCounts.Keys)
                {
                    if (!_appState.ExtensionFilters.ContainsKey(ext))
                    {
                        _appState.ExtensionFilters[ext] = true; // Default to visible for new extensions
                    }
                }

                // Sync ExtensionFilters: Remove extensions that no longer exist in the folder
                var unusedExtensions = _appState.ExtensionFilters.Keys
                    .Where(k => !extensionCounts.ContainsKey(k))
                    .ToList();

                foreach (var ext in unusedExtensions)
                {
                    _appState.ExtensionFilters.Remove(ext);
                }

                _appState.NotifyStateChanged(nameof(AppState.ExtensionFilters));
                _appState.NotifyStateChanged(nameof(AppState.ExtensionCounts));

                return rootNode.Children.Any() ? new List<FileNode> { rootNode } : new List<FileNode>();
            });
        }

        private void BuildIgnoreList(string rootPath)
        {

            _simpleNameIgnores.Clear();
            _relativePathIgnores.Clear();
            _wildcardRegexes.Clear();

            var allPatterns = new List<string>();
            var gitignorePath = Path.Combine(rootPath, ".gitignore");
            if (File.Exists(gitignorePath))
            {
                try
                {
                    allPatterns.AddRange(File.ReadAllLines(gitignorePath));
                }
                catch { }
            }
            allPatterns.AddRange(_appState.IgnorePatternsRaw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

            var uniquePatterns = allPatterns
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p) && !p.StartsWith("#"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var p in uniquePatterns)
            {
                if (p.Contains('*') || p.Contains('?'))
                {
                    try
                    {
                        var regex = new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                        _wildcardRegexes.Add(regex);
                    }
                    catch { }
                }
                else if (p.Contains('/') || p.Contains('\\'))
                {
                    _relativePathIgnores.Add(p.Replace('\\', '/').TrimEnd('/'));
                }
                else
                {
                    _simpleNameIgnores.Add(p);
                }
            }
        }

        private void ScanDirectoryRecursively(FileNode parentNode, Dictionary<string, int> extensionCounts)
        {
            try
            {
                var directoryInfo = new DirectoryInfo(parentNode.FullPath);

                foreach (var dir in directoryInfo.GetDirectories())
                {
                    if (IsIgnored(dir.FullName, isDir: true)) continue;

                    var dirNode = new FileNode { Name = dir.Name, FullPath = dir.FullName, IsDirectory = true, Parent = parentNode };
                    ScanDirectoryRecursively(dirNode, extensionCounts);
                    
                    // Only add directory if it has children (folders or filtered files)
                    if (dirNode.Children.Any())
                    {
                        parentNode.Children.Add(dirNode);
                    }
                }

                foreach (var file in directoryInfo.GetFiles())
                {
                    if (IsIgnored(file.FullName, isDir: false)) continue;

                    var ext = file.Extension.ToLowerInvariant();
                    if (string.IsNullOrEmpty(ext)) ext = ".noext";

                    // Count the extension regardless of filter state
                    if (!extensionCounts.ContainsKey(ext))
                    {
                        extensionCounts[ext] = 0;
                    }
                    extensionCounts[ext]++;

                    // Determine visibility based on current filters (or default to true if new)
                    bool isVisible = true;
                    if (_appState.ExtensionFilters.TryGetValue(ext, out var storedValue))
                    {
                        isVisible = storedValue;
                    }

                    if (isVisible)
                    {
                        var fileNode = new FileNode { Name = file.Name, FullPath = file.FullName, IsDirectory = false, Parent = parentNode };
                        parentNode.Children.Add(fileNode);
                    }
                }

                parentNode.Children = parentNode.Children.OrderBy(c => !c.IsDirectory).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (UnauthorizedAccessException)
            {
            }
        }


        private bool IsIgnored(string fullPath, bool isDir)
        {
            string relativePath;
            try
            {
                relativePath = Path.GetRelativePath(_appState.ProjectRoot, fullPath).Replace('\\', '/');
            }
            catch { return false; }

            var name = Path.GetFileName(fullPath);

            if (_simpleNameIgnores.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

            foreach (var relPattern in _relativePathIgnores)
            {
                if (relativePath.Equals(relPattern, StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith(relPattern + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var regex in _wildcardRegexes)
            {
                if (regex.IsMatch(name)) return true;
            }

            return false;
        }
    }
}