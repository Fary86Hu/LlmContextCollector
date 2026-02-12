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

            // Fájl fejlécek keresése (Új Fájl: ... vagy Fájl: ...)
            var headerRegex = new Regex(@"(?:^|\n)(?<type>Új Fájl|Fájl):\s*(?<path>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var matches = headerRegex.Matches(text);

            string globalExplanation = "";
            string nextFileExplanation = "";

            // 1. Globális magyarázat kinyerése az első fájl előtt
            if (matches.Count > 0)
            {
                var preamble = text.Substring(0, matches[0].Index);
                (globalExplanation, nextFileExplanation) = ExtractExplanationAndLog(preamble);
            }
            else
            {
                globalExplanation = text.Trim();
            }

            // 2. Fájlok feldolgozása és összefésülése (kumulatív mód)
            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var typeStr = match.Groups["type"].Value;
                var path = match.Groups["path"].Value.Trim().Replace('\\', '/');
                var status = typeStr.StartsWith("Új", StringComparison.OrdinalIgnoreCase) ? DiffStatus.New : DiffStatus.Modified;

                int contentStart = match.Index + match.Length;
                int contentEnd = (i == matches.Count - 1) ? text.Length : matches[i + 1].Index;

                string rawBlock = text.Substring(contentStart, contentEnd - contentStart);
                var (codePart, nextLog) = ExtractExplanationAndLog(rawBlock, looksForLogAtEnd: true);
                string cleanCode = RemoveMarkdownFences(codePart);

                if (parsedFilesDict.TryGetValue(path, out var existing))
                {
                    // Ha ugyanaz a fájl többször szerepel, összefűzzük a tartalmat (pl. több patch blokk)
                    existing.NewContent += "\n" + cleanCode;
                    if (!string.IsNullOrWhiteSpace(nextFileExplanation))
                        existing.Explanation += "\n" + nextFileExplanation;
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

        private (string content, string log) ExtractExplanationAndLog(string text, bool looksForLogAtEnd = false)
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