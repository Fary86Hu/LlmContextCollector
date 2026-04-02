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
            public string NewContent { get; set; } = "";
            public DiffStatus Status { get; set; }
            public string Explanation { get; set; } = "";
        }

        public (string GlobalExplanation, List<ParsedFile> ParsedFiles) ParseResponse(string text)
        {
            var parsedFilesDict = new Dictionary<string, ParsedFile>(StringComparer.OrdinalIgnoreCase);

            var headerRegex = new Regex(@"^(?<type>Új Fájl|Fájl|Törölt Fájl):\s*(?<path>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var matches = headerRegex.Matches(text);

            string globalExplanation = "";
            string nextFileExplanation = "";

            if (matches.Count > 0)
            {
                var preamble = text.Substring(0, matches[0].Index);
                (globalExplanation, nextFileExplanation) = ExtractExplanationAndLog(preamble);
            }
            else
            {
                globalExplanation = text.Trim();
            }

            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var typeStr = match.Groups["type"].Value;
                var rawPath = match.Groups["path"].Value.Trim().Replace('\\', '/');
                
                var path = rawPath.StartsWith("./") ? rawPath.Substring(2) : rawPath;
                path = path.TrimStart('/');

                var status = typeStr.StartsWith("Új", StringComparison.OrdinalIgnoreCase) ? DiffStatus.New : 
                             typeStr.StartsWith("Törölt", StringComparison.OrdinalIgnoreCase) ? DiffStatus.Deleted : 
                             DiffStatus.Modified;

                int contentStart = match.Index + match.Length;
                int contentEnd = (i == matches.Count - 1) ? text.Length : matches[i + 1].Index;

                string rawBlock = text.Substring(contentStart, contentEnd - contentStart);
                var (codePart, nextLog) = ExtractExplanationAndLog(rawBlock);
                string cleanCode = status == DiffStatus.Deleted ? string.Empty : RemoveMarkdownFences(codePart);

                if (parsedFilesDict.TryGetValue(path, out var existing))
                {
                    if (!existing.NewContent.Contains(cleanCode))
                    {
                        existing.NewContent = (existing.NewContent + "\n" + cleanCode).Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(nextFileExplanation))
                        existing.Explanation = (existing.Explanation + "\n" + nextFileExplanation).Trim();
                }
                else
                {
                    parsedFilesDict[path] = new ParsedFile
                    {
                        Path = path,
                        NewContent = cleanCode,
                        Status = status,
                        Explanation = nextFileExplanation
                    };
                }

                nextFileExplanation = nextLog;
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
                string content = text.Substring(0, logStart).Trim();
                return (content, log);
            }

            return (text.Trim(), "");
        }

        private string RemoveMarkdownFences(string code)
        {
            code = code.Trim();
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
            return code.Trim();
        }
    }
}