using LlmContextCollector.Models;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;

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
            var parsedFiles = new List<ParsedFile>();

            // Támogatjuk mind a keretezett (```code```), mind a keret nélküli (nyers) formátumot.
            // A regex két csoportot használ alternatívaként a tartalomhoz, mindkettő 'code' néven (NET Regex feature).
            var fileBlockRegex = new Regex(
                @"(?:^|\n)(?:Új Fájl|Fájl):\s*(?<path>[^\r\n]+)\s*(?:(?:```[a-zA-Z0-9]*\r?\n(?<code>[\s\S]*?)```)|(?<code>[\s\S]*?))(?=\s*(?:Új Fájl|Fájl|\[CHANGE_LOG\])|$|\z)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var changeLogRegex = new Regex(@"\[CHANGE_LOG\](.*?)\[/CHANGE_LOG\]", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var matches = fileBlockRegex.Matches(text);

            string globalExplanation = "";
            int lastMatchEndIndex = 0;

            if (matches.Count > 0)
            {
                var firstMatchIndex = matches[0].Index;
                var preText = text.Substring(0, firstMatchIndex);

                var logMatch = changeLogRegex.Match(preText);
                if (logMatch.Success)
                {
                    globalExplanation = preText.Substring(0, logMatch.Index).Trim();
                }
                else
                {
                    globalExplanation = preText.Trim();
                }
            }
            else
            {
                globalExplanation = text.Trim();
            }

            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var path = match.Groups["path"].Value.Trim().Replace('\\', '/');
                var code = match.Groups["code"].Value.Trim();

                var status = match.Value.TrimStart().StartsWith("Új", StringComparison.OrdinalIgnoreCase)
                             ? DiffStatus.New
                             : DiffStatus.Modified;

                string explanation = "";

                int searchStart = (i == 0) ? 0 : matches[i - 1].Index + matches[i - 1].Length;
                int searchEnd = match.Index;

                if (searchEnd > searchStart)
                {
                    string gapText = text.Substring(searchStart, searchEnd - searchStart);
                    var logMatch = changeLogRegex.Match(gapText);
                    if (logMatch.Success)
                    {
                        explanation = logMatch.Groups[1].Value.Trim();
                    }
                    else
                    {
                        if (i > 0 && !string.IsNullOrWhiteSpace(gapText))
                        {
                            explanation = gapText.Trim();
                        }
                    }
                }

                parsedFiles.Add(new ParsedFile
                {
                    Path = path,
                    NewContent = code,
                    Status = status,
                    Explanation = explanation
                });
            }

            return (globalExplanation, parsedFiles);
        }
    }
}