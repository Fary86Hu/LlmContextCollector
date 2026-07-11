using LlmContextCollector.Models;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System;

namespace LlmContextCollector.Services
{
    public class LlmResponseParserService
    {
        public class ParsedFile
        {
            public string Path { get; set; } = "";
            public string? OldPath { get; set; }
            public string NewContent { get; set; } = "";
            public DiffStatus Status { get; set; }
            public string Explanation { get; set; } = "";
        }

        public (string GlobalExplanation, List<ParsedFile> ParsedFiles) ParseResponse(string text)
        {
            var parsedFilesDict = new Dictionary<string, ParsedFile>(StringComparer.OrdinalIgnoreCase);

            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            var fileActionRegex = new Regex(
                @"(?s)<file_action(?<attrs>[^>]+)>(?<content>.*?)<\/file_action>",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            var matches = fileActionRegex.Matches(text);

            string globalExplanation = "";
            if (matches.Count > 0)
            {
                globalExplanation = text.Substring(0, matches[0].Index).Trim();
            }
            else
            {
                globalExplanation = text.Trim();
            }

            foreach (Match match in matches)
            {
                var attrs = match.Groups["attrs"].Value;
                var rawContent = match.Groups["content"].Value.Trim();

                var pathMatch = Regex.Match(attrs, @"path=[""'](?<val>[^""']+)[""']", RegexOptions.IgnoreCase);
                var statusMatch = Regex.Match(attrs, @"status=[""'](?<val>[^""']+)[""']", RegexOptions.IgnoreCase);
                var oldPathMatch = Regex.Match(attrs, @"old_path=[""'](?<val>[^""']+)[""']", RegexOptions.IgnoreCase);

                if (!pathMatch.Success || !statusMatch.Success)
                {
                    continue;
                }

                var path = pathMatch.Groups["val"].Value.Trim().Trim(' ', '\t', '*', '_', '`', '"', '\'').Replace('\\', '/');
                path = path.StartsWith("./") ? path.Substring(2) : path;
                path = path.TrimStart('/');

                var statusStr = statusMatch.Groups["val"].Value.Trim();
                var status = DiffStatus.Modified;

                if (statusStr.Equals("new", StringComparison.OrdinalIgnoreCase)) status = DiffStatus.New;
                else if (statusStr.Equals("deleted", StringComparison.OrdinalIgnoreCase)) status = DiffStatus.Deleted;
                else if (statusStr.Equals("renamed", StringComparison.OrdinalIgnoreCase)) status = DiffStatus.Renamed;

                string? oldPath = null;
                if (status == DiffStatus.Renamed && oldPathMatch.Success)
                {
                    oldPath = oldPathMatch.Groups["val"].Value.Trim().Trim(' ', '\t', '*', '_', '`', '"', '\'').Replace('\\', '/');
                    oldPath = oldPath.StartsWith("./") ? oldPath.Substring(2) : oldPath;
                    oldPath = oldPath.TrimStart('/');
                }

                string fileExplanation = "";
                string codePart = rawContent;

                var codeStartIdx = FindCodeStart(rawContent);
                if (codeStartIdx != -1)
                {
                    fileExplanation = rawContent.Substring(0, codeStartIdx).Trim();
                    codePart = rawContent.Substring(codeStartIdx);
                }

                string cleanCode = status == DiffStatus.Deleted ? string.Empty : RemoveMarkdownFences(codePart);

                if (parsedFilesDict.TryGetValue(path, out var existing))
                {
                    existing.NewContent = (existing.NewContent + "\n" + cleanCode);
                    if (!string.IsNullOrWhiteSpace(fileExplanation))
                        existing.Explanation = (existing.Explanation + "\n" + fileExplanation).Trim();
                }
                else
                {
                    parsedFilesDict[path] = new ParsedFile
                    {
                        Path = path,
                        OldPath = oldPath,
                        NewContent = cleanCode,
                        Status = status,
                        Explanation = fileExplanation
                    };
                }
            }

            return (globalExplanation, parsedFilesDict.Values.ToList());
        }

        private int FindCodeStart(string block)
        {
            var fenceIdx = block.IndexOf("```");
            var patchIdx = block.IndexOf("<<<<<<< SEARCH");

            if (fenceIdx != -1 && patchIdx != -1) return Math.Min(fenceIdx, patchIdx);
            if (fenceIdx != -1) return fenceIdx;
            return patchIdx;
        }

        private string RemoveMarkdownFences(string code)
        {
            code = code.Trim('\n', '\r');

            if (code.Contains("<<<<<<< SEARCH"))
            {
                var lines = code.Split('\n');
                var cleanLines = new List<string>();
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("```"))
                    {
                        continue;
                    }
                    cleanLines.Add(line.TrimEnd('\r'));
                }
                return string.Join("\n", cleanLines).Trim('\n', '\r', ' ', '\t');
            }

            if (code.StartsWith("```"))
            {
                int firstNewLine = code.IndexOf('\n');
                if (firstNewLine != -1)
                {
                    code = code.Substring(firstNewLine + 1);

                    int lastFence = code.LastIndexOf("```");
                    if (lastFence != -1)
                    {
                        code = code.Substring(0, lastFence);
                    }
                }
            }

            return code.TrimEnd('\n', '\r', ' ', '\t');
        }

        public List<string> ExtractPotentialFilePaths(string text, IEnumerable<string> allProjectFiles)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var words = Regex.Split(text, @"[\s`'""*()\[\]\n\r]+");

            var fileSet = new HashSet<string>(allProjectFiles, StringComparer.OrdinalIgnoreCase);

            foreach (var word in words)
            {
                var cleanWord = word.Trim().TrimEnd('.', ',', ';', ':').Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrEmpty(cleanWord) || cleanWord.Length < 3) continue;

                if (fileSet.Contains(cleanWord))
                {
                    results.Add(cleanWord);
                }
                else
                {
                    var fileName = Path.GetFileName(cleanWord);
                    if (!string.IsNullOrEmpty(fileName) && fileName.Contains('.'))
                    {
                        var match = allProjectFiles.FirstOrDefault(f => f.EndsWith(cleanWord, StringComparison.OrdinalIgnoreCase));
                        if (match != null) results.Add(match);
                    }
                }
            }

            var lineMatches = Regex.Matches(text, @"[*•-]\s*(?<path>[a-zA-Z0-9_\-\./\\]+\.[a-zA-Z0-9]{1,5})", RegexOptions.Multiline);
            foreach (Match match in lineMatches)
            {
                var p = match.Groups["path"].Value.Replace('\\', '/').TrimStart('/');
                var hit = allProjectFiles.FirstOrDefault(f => f.EndsWith(p, StringComparison.OrdinalIgnoreCase));
                if (hit != null) results.Add(hit);
            }

            return results.ToList();
        }

        public List<FileContextRequest> ExtractFileContextRequests(string text, IEnumerable<string> allProjectFiles)
        {
            var results = new List<FileContextRequest>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileSet = new HashSet<string>(allProjectFiles, StringComparer.OrdinalIgnoreCase);

            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var words = Regex.Split(line, @"[\s`'""*()\[\]\r]+");
                string? foundPath = null;

                foreach (var word in words)
                {
                    var cleanWord = word.Trim().TrimEnd('.', ',', ';', ':').Replace('\\', '/').TrimStart('/');
                    if (string.IsNullOrEmpty(cleanWord) || cleanWord.Length < 3) continue;

                    if (fileSet.Contains(cleanWord))
                    {
                        foundPath = cleanWord;
                        break;
                    }
                    else
                    {
                        var fileName = Path.GetFileName(cleanWord);
                        if (!string.IsNullOrEmpty(fileName) && fileName.Contains('.'))
                        {
                            var match = allProjectFiles.FirstOrDefault(f => f.EndsWith(cleanWord, StringComparison.OrdinalIgnoreCase));
                            if (match != null)
                            {
                                foundPath = match;
                                break;
                            }
                        }
                    }
                }

                if (foundPath != null)
                {
                    if (seenPaths.Add(foundPath))
                    {
                        bool includeRefs = line.Contains("[REFS]", StringComparison.OrdinalIgnoreCase) || line.Contains("[REF]", StringComparison.OrdinalIgnoreCase);
                        bool includeReferencing = line.Contains("[REFERENCING]", StringComparison.OrdinalIgnoreCase) || line.Contains("[BACKREF]", StringComparison.OrdinalIgnoreCase);

                        results.Add(new FileContextRequest
                        {
                            Path = foundPath,
                            IncludeReferences = includeRefs,
                            IncludeReferencing = includeReferencing
                        });
                    }
                }
            }

            return results;
        }
    }
}