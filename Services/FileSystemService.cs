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
                ScanDirectoryRecursively(rootNode);
                // Ha a gyökérmappának nincsenek gyermekei a szűrés után, üres listát adunk vissza,
                // hogy a felületen ne jelenjen meg egy üres, kibonthatatlan gyökérelem.
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
                catch { /* Hiba a .gitignore olvasásakor, figyelmen kívül hagyjuk */ }
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
                    catch { /* Invalid regex pattern */ }
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

        private void ScanDirectoryRecursively(FileNode parentNode)
        {
            try
            {
                var directoryInfo = new DirectoryInfo(parentNode.FullPath);

                // Könyvtárak feldolgozása "pruning" (lenyesés) logikával
                foreach (var dir in directoryInfo.GetDirectories())
                {
                    if (IsIgnored(dir.FullName, isDir: true)) continue;

                    var dirNode = new FileNode { Name = dir.Name, FullPath = dir.FullName, IsDirectory = true, Parent = parentNode };
                    ScanDirectoryRecursively(dirNode);
                    parentNode.Children.Add(dirNode);
                }

                // Fájlok feldolgozása a jelenlegi mappában
                var activeExtensions = _appState.ExtensionFilters
                    .Where(kvp => kvp.Value)
                    .Select(kvp => kvp.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var file in directoryInfo.GetFiles())
                {
                    if (!activeExtensions.Contains(file.Extension) || IsIgnored(file.FullName, isDir: false)) continue;

                    var fileNode = new FileNode { Name = file.Name, FullPath = file.FullName, IsDirectory = false, Parent = parentNode };
                    parentNode.Children.Add(fileNode);
                }

                parentNode.Children = parentNode.Children.OrderBy(c => !c.IsDirectory).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                // Nem tudjuk olvasni a mappát, kihagyjuk
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

            // Egyszerű név alapú egyezés (gyors)
            if (_simpleNameIgnores.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

            // Relatív útvonal alapú egyezés
            foreach (var relPattern in _relativePathIgnores)
            {
                if (relativePath.Equals(relPattern, StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith(relPattern + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Wildcard alapú egyezés (lassabb)
            foreach (var regex in _wildcardRegexes)
            {
                if (regex.IsMatch(name)) return true;
            }

            return false;
        }
    }
}