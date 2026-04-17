using LlmContextCollector.Models;
using System.Text.RegularExpressions;

namespace LlmContextCollector.Services
{
    public class FileSystemService
    {
        private readonly AppState _appState;
        private readonly AppLogService _logService;
        private List<string> _simpleNameIgnores = new();

        public FileSystemService(AppState appState, AppLogService logService)
        {
            _appState = appState;
            _logService = logService;
        }
        private List<string> _relativePathIgnores = new();
        private List<Regex> _wildcardRegexes = new();

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".svg", ".webp",
            ".exe", ".dll", ".so", ".dylib", ".bin", ".obj", ".lib", ".pdb",
            ".zip", ".rar", ".7z", ".tar", ".gz",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".woff", ".woff2", ".ttf", ".eot",
            ".mp3", ".wav", ".mp4", ".mov", ".avi", ".wmv", ".flv"
        };

        public FileSystemService(AppState appState)
        {
            _appState = appState;
        }

        public Task<List<FileNode>> ScanDirectoryAsync(string rootPath)
        {
            return Task.Run(() =>
            {
                _logService.LogInfo("FileSystem", "Könyvtár szkennelés indítva", rootPath);
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

                _appState.ExtensionCounts = extensionCounts;

                foreach (var ext in extensionCounts.Keys)
                {
                    if (!_appState.ExtensionFilters.ContainsKey(ext))
                    {
                        _appState.ExtensionFilters[ext] = true;
                    }
                }

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
            var userPatterns = _appState.Exclusions
                .Where(e => e.IsEnabled)
                .Select(e => e.Pattern);
            
            allPatterns.AddRange(userPatterns);

            var uniquePatterns = allPatterns
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p) && !p.StartsWith("#"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logService.LogInfo("FileSystem", "Kizárási lista összeállítva", $"{uniquePatterns.Count} egyedi minta alapján.");
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
                var children = new List<FileNode>();

                foreach (var dir in directoryInfo.GetDirectories())
                {
                    if (IsIgnored(dir.FullName, isDir: true)) continue;

                    var dirNode = new FileNode { Name = dir.Name, FullPath = dir.FullName, IsDirectory = true, Parent = parentNode };
                    ScanDirectoryRecursively(dirNode, extensionCounts);
                    
                    if (dirNode.Children.Count > 0)
                    {
                        children.Add(dirNode);
                    }
                }

                foreach (var file in directoryInfo.GetFiles())
                {
                    if (IsIgnored(file.FullName, isDir: false)) continue;
                    if (BinaryExtensions.Contains(file.Extension)) continue;

                    var ext = file.Extension.ToLowerInvariant();
                    if (string.IsNullOrEmpty(ext)) ext = ".noext";

                    if (!extensionCounts.TryGetValue(ext, out int currentCount))
                    {
                        extensionCounts[ext] = 1;
                    }
                    else
                    {
                        extensionCounts[ext] = currentCount + 1;
                    }

                    bool isVisible = true;
                    if (_appState.ExtensionFilters.TryGetValue(ext, out var storedValue))
                    {
                        isVisible = storedValue;
                    }

                    if (isVisible)
                    {
                        var fileNode = new FileNode { Name = file.Name, FullPath = file.FullName, IsDirectory = false, Parent = parentNode };
                        children.Add(fileNode);
                    }
                }

                parentNode.Children = children.OrderBy(c => !c.IsDirectory).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Access denied scanning directory: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning directory: {ex.Message}");
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