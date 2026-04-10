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

            var headerRegex = new Regex(@"^(?:Új Fájl|Fájl|Törölt Fájl|Átnevezett Fájl):\s*(?<path>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var headerMatches = headerRegex.Matches(text);

            string globalExplanation = "";
            string nextFileExplanation = "";

            if (headerMatches.Count > 0)
            {
                var preamble = text.Substring(0, headerMatches[0].Index);
                (globalExplanation, nextFileExplanation) = ExtractExplanationAndLog(preamble);
            }
            else
            {
                globalExplanation = text.Trim();
            }

            for (int i = 0; i < headerMatches.Count; i++)
            {
                var match = headerMatches[i];
                var fullLine = match.Value;
                var rawPath = match.Groups["path"].Value.Trim().Replace('\\', '/');

                var path = rawPath.StartsWith("./") ? rawPath.Substring(2) : rawPath;
                path = path.TrimStart('/');

                var status = DiffStatus.Modified;
                string? oldPath = null;

                if (fullLine.StartsWith("Új", StringComparison.OrdinalIgnoreCase)) status = DiffStatus.New;
                else if (fullLine.StartsWith("Törölt", StringComparison.OrdinalIgnoreCase)) status = DiffStatus.Deleted;
                else if (fullLine.StartsWith("Átnevezett", StringComparison.OrdinalIgnoreCase))
                {
                    status = DiffStatus.Renamed;
                    var pathParts = rawPath.Split(new[] { "->" }, StringSplitOptions.None);
                    if (pathParts.Length == 2)
                    {
                        oldPath = pathParts[0].Trim().TrimStart('/', '.').Replace('\\', '/');
                        path = pathParts[1].Trim().TrimStart('/', '.').Replace('\\', '/');
                    }
                }

                int contentStart = match.Index + match.Length;
                int contentEnd = (i == headerMatches.Count - 1) ? text.Length : headerMatches[i + 1].Index;

                string rawBlock = text.Substring(contentStart, contentEnd - contentStart);
                var (codePart, fileLog) = ExtractExplanationAndLog(rawBlock);
                string cleanCode = status == DiffStatus.Deleted ? string.Empty : RemoveMarkdownFences(codePart);

                string combinedExplanation = (nextFileExplanation + "\n" + fileLog).Trim();

                if (parsedFilesDict.TryGetValue(path, out var existing))
                {
                    existing.NewContent = (existing.NewContent + "\n" + cleanCode);
                    if (!string.IsNullOrWhiteSpace(combinedExplanation))
                        existing.Explanation = (existing.Explanation + "\n" + combinedExplanation).Trim();
                }
                else
                {
                    parsedFilesDict[path] = new ParsedFile
                    {
                        Path = path,
                        OldPath = oldPath,
                        NewContent = cleanCode,
                        Status = status,
                        Explanation = combinedExplanation
                    };
                }

                nextFileExplanation = "";
            }

            return (globalExplanation, parsedFilesDict.Values.ToList());
        }

        private (string content, string log) ExtractExplanationAndLog(string text)
        {
            var logStartMarker = "[CHANGE_LOG]";
            var logEndMarker = "[/CHANGE_LOG]";

            int logStart = text.LastIndexOf(logStartMarker, StringComparison.OrdinalIgnoreCase);
            int logEnd = text.LastIndexOf(logEndMarker, StringComparison.OrdinalIgnoreCase);

            if (logStart != -1 && logEnd != -1 && logEnd > logStart)
            {
                string log = text.Substring(logStart + logStartMarker.Length, logEnd - (logStart + logStartMarker.Length)).Trim();
                string content = text.Substring(0, logStart);
                return (content, log);
            }

            return (text, "");
        }

        private string RemoveMarkdownFences(string code)
        {
            code = code.Trim('\n', '\r');

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
    }
}
