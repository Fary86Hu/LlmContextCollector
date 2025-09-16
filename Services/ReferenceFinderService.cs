using LlmContextCollector.Models;
using System.Text.RegularExpressions;

namespace LlmContextCollector.Services
{
    public class ReferenceFinderService
    {
        private static readonly Regex CSharpKeywordsRegex = new Regex(@"\b(public|private|protected|internal|static|class|struct|interface|enum|void|string|int|bool|double|float|decimal|long|short|byte|var|get|set|new|using|namespace|return|if|else|for|foreach|while|do|switch|case|default|break|continue|try|catch|finally|throw|lock|using|yield|base|this|true|false|null|async|await|partial|readonly|virtual|override|sealed|abstract|as|is|in|out|ref|params|checked|unchecked|unsafe|fixed|stackalloc)\b", RegexOptions.Compiled);
        private static readonly Regex CSharpCommonTypesRegex = new Regex(@"\b(object|string|int|bool|double|float|decimal|long|short|byte|List|Dictionary|IEnumerable|Task|IActionResult|ICollection|Exception|PageModel|ComponentBase|DbContext|WebApplication|Program|HttpContext|IServiceCollection|IConfiguration|ILogger|Activator|Attribute|EventArgs|Console|Math|DateTime|Guid|CancellationToken|TaskCompletionSource|Action|Func|Predicate|Tuple|ValueTuple)\b", RegexOptions.Compiled);
        private static readonly Regex PotentialTypeRegex = new Regex(@"\b[A-Z][a-zA-Z0-9_]*\b(?:<[A-Za-z0-9_,\s<>]+>)?", RegexOptions.Compiled);


        public async Task<List<string>> FindReferencesAsync(List<string> startingFilesRel, List<FileNode> allNodes, string projectRoot, int depth)
        {
            var allFoundFilesRel = new HashSet<string>();
            var allScannedFilesRel = new HashSet<string>();
            var filesToScanNextRel = new HashSet<string>(startingFilesRel);

            var allProjectFiles = new List<FileNode>();
            GetAllFileNodes(allNodes, allProjectFiles);

            for (int i = 0; i < depth; i++)
            {
                if (!filesToScanNextRel.Any()) break;

                var currentLevelScan = filesToScanNextRel.Except(allScannedFilesRel).ToList();
                filesToScanNextRel.Clear();
                
                var potentialTypeNames = new HashSet<string>();

                foreach (var fileRelPath in currentLevelScan)
                {
                    allScannedFilesRel.Add(fileRelPath);
                    
                    var fullPath = Path.Combine(projectRoot, fileRelPath);
                    if (!File.Exists(fullPath)) continue;

                    try
                    {
                        var content = await File.ReadAllTextAsync(fullPath);
                        var matches = PotentialTypeRegex.Matches(content);
                        foreach (Match match in matches.Cast<Match>())
                        {
                            // A C# regex nem tartalmazza a generikusokat, így azokat manuálisan kell eltávolítani
                            var construct = match.Value;
                            var cleanConstruct = Regex.Replace(construct, @"<.*?>", "");

                            if (!string.IsNullOrWhiteSpace(cleanConstruct) &&
                                !CSharpKeywordsRegex.IsMatch(cleanConstruct) &&
                                !CSharpCommonTypesRegex.IsMatch(cleanConstruct))
                            {
                                potentialTypeNames.Add(cleanConstruct);
                            }
                        }
                    }
                    catch { /* Ignore read errors */ }
                }

                if (!potentialTypeNames.Any()) continue;
                
                var extendedTypeNames = new HashSet<string>(potentialTypeNames);
                foreach (var name in potentialTypeNames)
                {
                    if (name.Length > 2 && name.StartsWith('I') && char.IsUpper(name[1]))
                    {
                        extendedTypeNames.Add(name.Substring(1));
                    }
                }

                foreach (var typeName in extendedTypeNames)
                {
                    var targetFileNames = new HashSet<string> { $"{typeName}.cs", $"{typeName}.razor", $"{typeName}.cshtml", $"I{typeName}.cs" };
                    var foundNodes = allProjectFiles.Where(f => targetFileNames.Contains(f.Name));
                    
                    foreach (var node in foundNodes)
                    {
                        var relPath = Path.GetRelativePath(projectRoot, node.FullPath).Replace('\\', '/');
                        if (!allScannedFilesRel.Contains(relPath))
                        {
                            filesToScanNextRel.Add(relPath);
                        }
                        allFoundFilesRel.Add(relPath);
                    }
                }
            }

            // Az eredetileg megadott fájlokat ne adjuk vissza az eredményben
            return allFoundFilesRel.Except(new HashSet<string>(startingFilesRel)).ToList();
        }

        private void GetAllFileNodes(IEnumerable<FileNode> nodes, List<FileNode> flatList)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory)
                {
                    GetAllFileNodes(node.Children, flatList);
                }
                else
                {
                    flatList.Add(node);
                }
            }
        }
    }
}